#!/usr/bin/env python3
"""
Generates the tiny stand-in Chatterbox Turbo graphs the unit tests run the real decode loop on
(no model downloads, no GPU). Run: python3 gen_tiny_turbo.py > ../Mock/TinyTurboModels.cs

The stand-ins keep the real graphs' interface and nothing else:
  encoder  audio_values -> cond_emb [1,4,4], prompt_token [1,5], ref_x_vector, prompt_feat (constants)
  embed    input_ids -> embeds [1,n,4]
  lm       inputs_embeds, attention_mask, position_ids, 24 x (past key, past value) -> logits, 24 x (present key, present value)
           the argmax token is (total_length % 97) + 100, and STOP (6562) once total_length >= 47, so the
           speech tokens depend on the key-value cache growing exactly as the real loop must feed it back
  lm_bad   same interface, fails at run time (a Reshape that cannot hold the logits)
  decoder  speech_tokens, speaker_embeddings, speaker_features -> wav [1,n] = tokens * 1e-4
  stream_decoder  like the real decoder: drops the 5 prompt tokens and returns 960 samples per remaining token,
           every sample of a token = token * 1e-4 (so a chunked decode can be checked against a whole one)
The KV cache element type is float (fp32 graphs) or float16 (fp16 / q4f16 graphs).
"""
import base64
import numpy as np
import onnx
from onnx import TensorProto as TP, helper as h, numpy_helper as nh

W = 4          # embedding width
V = 6563       # vocabulary (speech tokens + START + STOP)
LAYERS = 24
HEADS, HD = 16, 64
STOP_AT = 47   # total length at which the stand-in LM emits STOP


def vi(name, elem, shape):
    return h.make_tensor_value_info(name, elem, shape)


def i64(name, value):
    return nh.from_array(np.array(value, dtype=np.int64), name)


def finish(graph):
    model = h.make_model(graph, opset_imports=[h.make_opsetid("", 17)])
    model.ir_version = 8
    onnx.checker.check_model(model)
    return model.SerializeToString()


def encoder():
    inits = [
        nh.from_array((np.arange(16, dtype=np.float32).reshape(1, 4, 4) * 0.05), "c_cond"),
        nh.from_array(np.array([[11, 12, 13, 14, 15]], dtype=np.int64), "c_prompt"),
        nh.from_array(np.array([[0.1, 0.2, 0.3, 0.4]], dtype=np.float32), "c_xvec"),
        nh.from_array(np.full((1, 6, 3), 0.25, dtype=np.float32), "c_feat"),
    ]
    nodes = [
        h.make_node("Identity", ["c_cond"], ["cond_emb"]),
        h.make_node("Identity", ["c_prompt"], ["prompt_token"]),
        h.make_node("Identity", ["c_xvec"], ["ref_x_vector"]),
        h.make_node("Identity", ["c_feat"], ["prompt_feat"]),
    ]
    g = h.make_graph(
        nodes, "encoder",
        [vi("audio_values", TP.FLOAT, [1, "N"])],
        [vi("cond_emb", TP.FLOAT, [1, 4, 4]), vi("prompt_token", TP.INT64, [1, 5]),
         vi("ref_x_vector", TP.FLOAT, [1, 4]), vi("prompt_feat", TP.FLOAT, [1, 6, 3])],
        inits)
    return finish(g)


def embed():
    table = nh.from_array(np.linspace(-1, 1, 16 * W, dtype=np.float32).reshape(16, W), "table")
    nodes = [
        h.make_node("Mod", ["input_ids", "c16"], ["ids16"]),
        h.make_node("Gather", ["table", "ids16"], ["inputs_embeds"], axis=0),
    ]
    g = h.make_graph(
        nodes, "embed",
        [vi("input_ids", TP.INT64, [1, "n"])],
        [vi("inputs_embeds", TP.FLOAT, [1, "n", W])],
        [table, i64("c16", 16)])
    return finish(g)


def lm(half, bad=False):
    kv = TP.FLOAT16 if half else TP.FLOAT
    inputs = [vi("inputs_embeds", TP.FLOAT, [1, "s", W]), vi("attention_mask", TP.INT64, [1, "t"]),
              vi("position_ids", TP.INT64, [1, "s"])]
    outputs = [vi("logits", TP.FLOAT, [1, "s", V])]
    nodes = []
    for layer in range(LAYERS):
        for kind in ("key", "value"):
            inputs.append(vi(f"past_key_values.{layer}.{kind}", kv, [1, HEADS, "p", HD]))
            outputs.append(vi(f"present.{layer}.{kind}", kv, [1, HEADS, "q", HD]))
    inits = [i64("c1", 1), i64("c2", 2), i64("c97", 97), i64("c100", 100), i64("c_stop_at", STOP_AT),
             i64("c_stop", 6562), i64("c_depth", V),
             nh.from_array(np.array([0.0, 10.0], dtype=np.float32), "c_values"),
             i64("v1", [1]), i64("v16", [HEADS]), i64("v64", [HD]), i64("vV", [V]), i64("v5", [5]),
             nh.from_array(np.array(0.5, dtype=np.float16 if half else np.float32), "c_half")]
    nodes += [
        h.make_node("Shape", ["past_key_values.0.key"], ["sh_past"]),
        h.make_node("Gather", ["sh_past", "c2"], ["p"], axis=0),
        h.make_node("Shape", ["inputs_embeds"], ["sh_e"]),
        h.make_node("Gather", ["sh_e", "c1"], ["s"], axis=0),
        h.make_node("Add", ["p", "s"], ["total"]),
        h.make_node("Mod", ["total", "c97"], ["m"]),
        h.make_node("Add", ["m", "c100"], ["t"]),
        h.make_node("GreaterOrEqual", ["total", "c_stop_at"], ["stop"]),
        h.make_node("Where", ["stop", "c_stop", "t"], ["token"]),
        h.make_node("Reshape", ["token", "v1"], ["token1"]),
        h.make_node("OneHot", ["token1", "c_depth", "c_values"], ["oh"], axis=-1),
        h.make_node("Reshape", ["oh", "shape_oh"], ["oh3"]),
        h.make_node("Reshape", ["s", "v1"], ["s1"]),
        h.make_node("Concat", ["v1", "s1", "vV"], ["out_shape"], axis=0),
        h.make_node("Concat", ["v1", "v16", "s1", "v64"], ["new_shape"], axis=0),
        h.make_node("ConstantOfShape", ["new_shape"], ["new_kv"],
                    value=nh.from_array(np.array([0.5], dtype=np.float16 if half else np.float32))),
    ]
    inits.append(i64("shape_oh", [1, 1, V]))
    if bad:
        # [s + 5] elements cannot hold V logits: fails when the graph runs, not when it loads.
        nodes += [
            h.make_node("Add", ["s1", "v5"], ["bad_shape"]),
            h.make_node("Reshape", ["oh3", "bad_shape"], ["bad_logits"]),
            h.make_node("Reshape", ["bad_logits", "out_shape"], ["logits_raw"]),
            h.make_node("Identity", ["logits_raw"], ["logits"]),
        ]
    else:
        nodes += [h.make_node("Expand", ["oh3", "out_shape"], ["logits"])]
    for layer in range(LAYERS):
        for kind in ("key", "value"):
            nodes.append(h.make_node("Concat", [f"past_key_values.{layer}.{kind}", "new_kv"],
                                     [f"present.{layer}.{kind}"], axis=2))
    g = h.make_graph(nodes, "lm", inputs, outputs, inits)
    return finish(g)


def decoder():
    nodes = [
        h.make_node("Cast", ["speech_tokens"], ["tok_f"], to=TP.FLOAT),
        h.make_node("Mul", ["tok_f", "scale"], ["wav"]),
    ]
    g = h.make_graph(
        nodes, "decoder",
        [vi("speech_tokens", TP.INT64, [1, "n"]), vi("speaker_embeddings", TP.FLOAT, [1, 4]),
         vi("speaker_features", TP.FLOAT, [1, 6, 3])],
        [vi("wav", TP.FLOAT, [1, "n"])],
        [nh.from_array(np.array(1e-4, dtype=np.float32), "scale")])
    return finish(g)


def stream_decoder():
    """Real-decoder shape: audio only for the tokens after the 5-token prompt, 960 samples each."""
    inits = [
        i64("start", [5]), i64("end", [1 << 40]), i64("axes", [1]),
        nh.from_array(np.array(1e-4, dtype=np.float32), "scale"),
        i64("m1", [-1]), i64("rep", [1, 1, 960]), i64("flat", [1, -1]),
        i64("last", [-1]),
    ]
    nodes = [
        h.make_node("Slice", ["speech_tokens", "start", "end", "axes"], ["new_tokens"]),
        h.make_node("Cast", ["new_tokens"], ["tok_f"], to=TP.FLOAT),
        h.make_node("Mul", ["tok_f", "scale"], ["val"]),
        h.make_node("Unsqueeze", ["val", "last"], ["val3"]),
        h.make_node("Tile", ["val3", "rep"], ["tiled"]),
        h.make_node("Reshape", ["tiled", "flat"], ["wav"]),
    ]
    g = h.make_graph(
        nodes, "stream_decoder",
        [vi("speech_tokens", TP.INT64, [1, "n"]), vi("speaker_embeddings", TP.FLOAT, [1, 4]),
         vi("speaker_features", TP.FLOAT, [1, 6, 3])],
        [vi("wav", TP.FLOAT, [1, "m"])],
        inits)
    return finish(g)


def b64(data):
    return base64.b64encode(data).decode()


def main():
    parts = {
        "Encoder": encoder(), "Embed": embed(), "Decoder": decoder(), "StreamDecoder": stream_decoder(),
        "Lm32": lm(False), "Lm16": lm(True), "LmBad32": lm(False, bad=True), "LmBad16": lm(True, bad=True),
    }
    print("// <auto-generated> by Fixtures/gen_tiny_turbo.py - do not edit. </auto-generated>")
    print("namespace AIVoiceActing.Tests.Mock;")
    print()
    print("/// <summary>Tiny stand-ins for the four Chatterbox Turbo graphs (see Fixtures/gen_tiny_turbo.py for what they compute).</summary>")
    print("public static class TinyTurboModels")
    print("{")
    for name, data in parts.items():
        print(f'    public static byte[] {name} {{ get; }} = Convert.FromBase64String("{b64(data)}");')
        print()
    print("}")


if __name__ == "__main__":
    main()
