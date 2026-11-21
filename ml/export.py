"""Re-export a checkpoint to ONNX (model.onnx + model-step.onnx), and check the cached step matches the full model.

  python export.py <folder with model.pt>
"""
import os
import sys

import torch

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from muse.model import load, export_onnx  # noqa: E402


def main():
    folder = sys.argv[1]
    model, _ = load(os.path.join(folder, "model.pt"), "cpu")
    model.eval()
    c = model.config
    tokens = torch.randint(5, 200, (2, 40))
    with torch.no_grad():
        full = model(tokens)[:, -1, :]
        past = torch.zeros((c.layers, 2, 2, c.heads, 0, c.dim // c.heads))
        logits, past = model.step(tokens[:, :30], past)
        for i in range(30, 40):
            logits, past = model.step(tokens[:, i:i + 1], past)
    diff = (full - logits).abs().max().item()
    print(f"cache check: max difference {diff:.2e}")
    if diff > 1e-3:
        print("cache check FAILED")
        return 1
    export_onnx(model, os.path.join(folder, "model.onnx"))
    print("exported", folder)
    return 0


if __name__ == "__main__":
    sys.exit(main())
