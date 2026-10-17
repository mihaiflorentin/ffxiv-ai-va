"""Generate the minimal ONNX repros for the WebGPU plugin EP abort ("Invalid memory type: -1").

usage: python3 genrepro.py <out-dir>      (needs `pip install onnx`)

  rangef.onnx              : single float Range node. Aborts on EP 0.2.1, 0.3.0, 0.4.0.
  range.onnx               : same with int64. Runs (falls back to CPU) unless the option enableInt64=1 is set,
                             then the WebGPU Range kernel is used and the process aborts.
  chain.onnx               : Add -> Mul -> Add. Runs fine on the EP. Run with the option
                             forceCpuNodeNames=mul1 (so ORT inserts MemcpyToHost/MemcpyFromHost around a CPU Mul)
                             and the process aborts on EP 0.2.1, 0.3.0, 0.4.0.
Run: Spike runmodel model=<out-dir>/chain.onnx device=webgpu dev=0 opts=dawnBackendType:Vulkan forcecpu=mul1
"""
import os
import sys

import onnx
from onnx import TensorProto as T
from onnx import helper as h

out = sys.argv[1] if len(sys.argv) > 1 else "."
os.makedirs(out, exist_ok=True)


def f(n, shp):
    return h.make_tensor_value_info(n, T.FLOAT, shp)


def i64(n, shp):
    return h.make_tensor_value_info(n, T.INT64, shp)


def save(name, nodes, ins, outs):
    g = h.make_graph(nodes, name, ins, outs)
    m = h.make_model(g, opset_imports=[h.make_opsetid("", 19)])
    m.ir_version = 9
    onnx.save(m, os.path.join(out, name + ".onnx"))


save("range", [h.make_node("Range", ["s", "e", "d"], ["y"])], [i64("s", []), i64("e", []), i64("d", [])], [i64("y", [None])])
save("rangef", [h.make_node("Range", ["s", "e", "d"], ["y"])], [f("s", []), f("e", []), f("d", [])], [f("y", [None])])
save(
    "chain",
    [
        h.make_node("Add", ["a", "b"], ["t1"], name="add1"),
        h.make_node("Mul", ["t1", "b"], ["t2"], name="mul1"),
        h.make_node("Add", ["t2", "a"], ["y"], name="add2"),
    ],
    [f("a", [2, 4]), f("b", [2, 4])],
    [f("y", [2, 4])],
)
print("wrote range.onnx rangef.onnx chain.onnx to", out)
