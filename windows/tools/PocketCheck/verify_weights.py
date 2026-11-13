"""Provenance check for the Pocket TTS ONNX files the Windows app downloads.

Every learned weight in the five ONNX graphs (KevinAHM/pocket-tts-onnx) must be one of
Kyutai's Pocket TTS weights, as the Mac uses them (mlx-community/pocket-tts, the MLX
conversion of kyutai/pocket-tts, bfloat16). An ONNX weight counts as Kyutai's when, rounded
to bfloat16, it equals a tensor of the Mac's model (in any axis order, flattened), within
one bfloat16 step. Whatever is left must be small and explainable (shapes, constants).

Usage (see README.md):
    .venv/bin/python -I verify_weights.py <folder with onnx/*.onnx> <mlx model.safetensors>
"""

import itertools
import sys
from collections import defaultdict
from pathlib import Path

import numpy as np
import onnx
from onnx import numpy_helper
from safetensors.numpy import load_file

GRAPHS = ["flow_lm_main", "flow_lm_flow", "mimi_encoder", "mimi_decoder", "text_conditioner"]


def to_bf16_bits(a: np.ndarray) -> np.ndarray:
    """float32 -> bfloat16 bit patterns, round to nearest even (as torch/MLX do)."""
    u = np.ascontiguousarray(a, dtype=np.float32).view(np.uint32)
    rounding = ((u >> 16) & 1) + 0x7FFF
    return ((u + rounding) >> 16).astype(np.uint16)


def bf16_as_float(bits: np.ndarray) -> np.ndarray:
    return (bits.astype(np.uint32) << 16).view(np.float32)


def main(onnx_dir: Path, mlx_file: Path) -> int:
    mac = load_file(str(mlx_file))
    # The Mac's tensors as bfloat16 bit patterns, indexed by element count.
    by_size = defaultdict(list)
    for name, t in mac.items():
        bits = t.view(np.uint16) if t.dtype != np.float32 else to_bf16_bits(t)
        by_size[t.size].append((name, bits.reshape(t.shape)))

    total_params = matched_params = 0
    used = set()
    leftovers = []
    for g in GRAPHS:
        path = onnx_dir / f"{g}.onnx"
        model = onnx.load(str(path))
        g_total = g_matched = 0
        for init in model.graph.initializer:
            arr = numpy_helper.to_array(init)
            if arr.dtype != np.float32 or arr.size < 2:
                continue
            g_total += arr.size
            bits = to_bf16_bits(arr)
            hit = None
            for name, cand in by_size.get(arr.size, []):
                # MLX keeps convolution weights as (out, kernel, in), ONNX as (out, in, kernel).
                views = [np.transpose(cand, perm).reshape(-1) for perm in itertools.permutations(range(cand.ndim))]
                for v in views:
                    diff = np.abs(bf16_as_float(v) - bf16_as_float(bits.reshape(-1)))
                    step = np.abs(bf16_as_float(v)) * 2.0 ** -7 + 1e-30
                    if np.all(diff <= step):
                        hit = name
                        break
                if hit:
                    break
            if hit:
                g_matched += arr.size
                used.add(hit)
            else:
                leftovers.append((g, init.name, list(arr.shape), arr.size))
        total_params += g_total
        matched_params += g_matched
        print(f"{g}: {g_matched:,} of {g_total:,} weights are Kyutai's ({100 * g_matched / max(1, g_total):.3f}%)")

    print(f"\nAll graphs: {matched_params:,} of {total_params:,} ({100 * matched_params / total_params:.3f}%)")
    print(f"Mac tensors used: {len(used)} of {len(mac)}")
    unused = [k for k in mac if k not in used]
    if unused:
        print("Mac tensors not found in the ONNX files (expected: the voice-cloning parts merged into others, or unused):")
        for k in unused:
            print("   ", k, mac[k].shape)
    if leftovers:
        print("ONNX weights with no match (must be constants or derived tensors):")
        for g, name, shape, n in leftovers:
            print(f"    {g}: {name} {shape} ({n:,})")
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(2)
    sys.exit(main(Path(sys.argv[1]), Path(sys.argv[2])))
