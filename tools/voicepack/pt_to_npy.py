#!/usr/bin/env python3
"""Convert a KVoiceWalk .pt voice tensor to the .npy the plugin reads.

Usage: pt_to_npy.py <in.pt> <out.npy>

Kokoro style tensors are float32 with shape (510, 1, 256). Run with the KVoiceWalk venv python.
"""
import sys

import numpy as np
import torch


def main() -> None:
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    tensor = torch.load(sys.argv[1], weights_only=True, map_location="cpu").float().numpy()
    if tensor.shape != (510, 1, 256):
        raise SystemExit(f"unexpected tensor shape {tensor.shape}; want (510, 1, 256)")
    np.save(sys.argv[2], tensor.astype(np.float32))
    print(f"saved {sys.argv[2]} {tensor.shape}")


if __name__ == "__main__":
    main()
