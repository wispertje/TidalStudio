"""A small decoder-only transformer over Tidepool Muse tokens (see Tokenizer.cs for the vocabulary)."""
from dataclasses import dataclass, asdict
import math

import torch
import torch.nn as nn
import torch.nn.functional as F


@dataclass
class Config:
    vocab_size: int = 256
    max_length: int = 1024
    layers: int = 8
    heads: int = 6
    dim: int = 384
    dropout: float = 0.1

    def to_dict(self):
        return asdict(self)


class Block(nn.Module):
    def __init__(self, c: Config):
        super().__init__()
        self.ln1 = nn.LayerNorm(c.dim)
        self.qkv = nn.Linear(c.dim, 3 * c.dim)
        self.proj = nn.Linear(c.dim, c.dim)
        self.ln2 = nn.LayerNorm(c.dim)
        self.mlp = nn.Sequential(nn.Linear(c.dim, 4 * c.dim), nn.GELU(), nn.Linear(4 * c.dim, c.dim))
        self.heads = c.heads
        self.dropout = c.dropout
        self.drop = nn.Dropout(c.dropout)

    def forward(self, x, past=None):
        """past: (keys, values) of earlier positions, [B, H, Tp, D] each. Returns (x, (keys, values)) then."""
        b, t, d = x.shape
        q, k, v = self.qkv(self.ln1(x)).split(d, dim=2)
        q = q.view(b, t, self.heads, d // self.heads).transpose(1, 2)
        k = k.view(b, t, self.heads, d // self.heads).transpose(1, 2)
        v = v.view(b, t, self.heads, d // self.heads).transpose(1, 2)
        if past is None:
            y = F.scaled_dot_product_attention(q, k, v, is_causal=True, dropout_p=self.dropout if self.training else 0.0)
            present = None
        else:
            k = torch.cat([past[0], k], dim=2)
            v = torch.cat([past[1], v], dim=2)
            tk = k.shape[2]
            # New position i (absolute tk - t + i) sees keys up to and including itself.
            mask = torch.arange(tk, device=x.device)[None, :] <= (torch.arange(t, device=x.device)[:, None] + (tk - t))
            y = F.scaled_dot_product_attention(q, k, v, attn_mask=mask)
            present = (k, v)
        y = y.transpose(1, 2).contiguous().view(b, t, d)
        x = x + self.drop(self.proj(y))
        x = x + self.drop(self.mlp(self.ln2(x)))
        return x if past is None else (x, present)


class Muse(nn.Module):
    def __init__(self, c: Config):
        super().__init__()
        self.config = c
        self.tok = nn.Embedding(c.vocab_size, c.dim)
        self.pos = nn.Embedding(c.max_length, c.dim)
        self.drop = nn.Dropout(c.dropout)
        self.blocks = nn.ModuleList([Block(c) for _ in range(c.layers)])
        self.ln = nn.LayerNorm(c.dim)
        self.head = nn.Linear(c.dim, c.vocab_size, bias=False)
        self.head.weight = self.tok.weight  # tied embeddings
        self.apply(self._init)
        for name, p in self.named_parameters():
            if name.endswith("proj.weight"):
                nn.init.normal_(p, mean=0.0, std=0.02 / math.sqrt(2 * c.layers))

    @staticmethod
    def _init(m):
        if isinstance(m, nn.Linear):
            nn.init.normal_(m.weight, mean=0.0, std=0.02)
            if m.bias is not None:
                nn.init.zeros_(m.bias)
        elif isinstance(m, nn.Embedding):
            nn.init.normal_(m.weight, mean=0.0, std=0.02)

    def forward(self, tokens):
        t = tokens.shape[1]
        pos = torch.arange(t, device=tokens.device)
        x = self.drop(self.tok(tokens) + self.pos(pos)[None, :, :])
        for block in self.blocks:
            x = block(x)
        return self.head(self.ln(x))

    def step(self, tokens, past):
        """Generation with a cache. past: [layers, 2, B, H, Tp, D] (Tp may be 0). Returns last logits and the new cache."""
        tp = past.shape[4]
        pos = torch.arange(tokens.shape[1], device=tokens.device) + tp
        x = self.tok(tokens) + self.pos(pos)[None, :, :]
        presents = []
        for i, block in enumerate(self.blocks):
            x, (k, v) = block(x, (past[i, 0], past[i, 1]))
            presents.append(torch.stack([k, v]))
        return self.head(self.ln(x[:, -1, :])), torch.stack(presents)

    def parameter_count(self):
        return sum(p.numel() for p in self.parameters())


class LastLogits(nn.Module):
    """For ONNX: only the logits of the last position (what generation needs)."""

    def __init__(self, model: Muse):
        super().__init__()
        self.model = model

    def forward(self, tokens):
        return self.model(tokens)[:, -1, :]


class Step(nn.Module):
    """For ONNX: one generation step with a key/value cache."""

    def __init__(self, model: Muse):
        super().__init__()
        self.model = model

    def forward(self, tokens, past):
        return self.model.step(tokens, past)


def save(model: Muse, path, extra=None):
    torch.save({"config": model.config.to_dict(), "state": model.state_dict(), "extra": extra or {}}, path)


def load(path, device="cpu"):
    data = torch.load(path, map_location=device, weights_only=False)
    model = Muse(Config(**data["config"]))
    model.load_state_dict(data["state"])
    return model.to(device), data.get("extra", {})


def export_onnx(model: Muse, path):
    model = model.eval().to("cpu")
    wrapper = LastLogits(model)
    example = torch.zeros((2, 24), dtype=torch.long)
    torch.onnx.export(
        wrapper, (example,), path,
        input_names=["tokens"], output_names=["logits"],
        dynamic_axes={"tokens": {0: "batch", 1: "length"}, "logits": {0: "batch"}},
        opset_version=17, dynamo=False,
    )
    # The cached version, used by TidalStudio for fast generation (model-step.onnx next to model.onnx).
    c = model.config
    step_path = path[:-5] + "-step.onnx" if path.endswith(".onnx") else path + "-step"
    past = torch.zeros((c.layers, 2, 2, c.heads, 5, c.dim // c.heads))
    torch.onnx.export(
        Step(model), (torch.zeros((2, 3), dtype=torch.long), past), step_path,
        input_names=["tokens", "past"], output_names=["logits", "present"],
        dynamic_axes={"tokens": {0: "batch", 1: "length"}, "past": {2: "batch", 4: "past_length"},
                      "logits": {0: "batch"}, "present": {2: "batch", 4: "total_length"}},
        opset_version=17, dynamo=False,
    )
