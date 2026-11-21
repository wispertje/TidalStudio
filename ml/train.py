"""Train (pretrain or fine-tune) the Tidepool Muse model. Started by TidalStudio; prints JSON lines on stdout.

  python train.py --data <pretrain> [--own <own>] --out <folder> [--init model.pt] [--steps N] ...

Writes <out>/model.pt (PyTorch), <out>/model.onnx (for TidalStudio) and <out>/info.json.
"""
import argparse
import json
import math
import os
import sys
import time

import torch
import torch.nn.functional as F

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from muse.model import Config, Muse, save, load, export_onnx  # noqa: E402
from muse import data as D  # noqa: E402


def emit(**kw):
    print(json.dumps(kw), flush=True)


def loss_of(model, x):
    # Only predict what comes after SEP (the part itself), not the conditioning header.
    logits = model(x[:, :-1])
    target = x[:, 1:].clone()
    sep = (x == 3).int().argmax(dim=1)  # position of SEP
    positions = torch.arange(target.shape[1], device=x.device)[None, :]
    target[positions < sep[:, None]] = 0
    return F.cross_entropy(logits.reshape(-1, logits.shape[-1]).float(), target.reshape(-1), ignore_index=0)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", help="pretrain dataset folder (MIDI)")
    ap.add_argument("--own", help="your own music dataset folder")
    ap.add_argument("--own-weight", type=float, default=4.0, help="how much more often own sequences are picked")
    ap.add_argument("--own-share", type=float, help="fraction of each batch from your own music (overrides --own-weight)")
    ap.add_argument("--out", required=True)
    ap.add_argument("--init", help="checkpoint to fine-tune from")
    ap.add_argument("--steps", type=int, default=20000)
    ap.add_argument("--batch", type=int, default=48)
    ap.add_argument("--max-tokens", type=int, default=24000)
    ap.add_argument("--lr", type=float, default=6e-4)
    ap.add_argument("--warmup", type=int, default=500)
    ap.add_argument("--eval-every", type=int, default=500)
    ap.add_argument("--layers", type=int, default=8)
    ap.add_argument("--dim", type=int, default=384)
    ap.add_argument("--heads", type=int, default=6)
    ap.add_argument("--cpu", action="store_true")
    args = ap.parse_args()

    device = "cuda" if torch.cuda.is_available() and not args.cpu else "cpu"
    os.makedirs(args.out, exist_ok=True)
    sets = []
    if D.exists(args.data):
        sets.append((D.Sequences(args.data), 1.0))
    if D.exists(args.own):
        own = D.Sequences(args.own)
        weight = args.own_weight
        if args.own_share and sets:
            # weight * n_own / (n_pre + weight * n_own) = share
            n_pre, n_own = len(sets[0][0]), max(1, len(own))
            weight = args.own_share * n_pre / ((1 - args.own_share) * n_own)
        sets.append((own, weight))
    if not sets:
        emit(event="error", message="no training data")
        return 1
    mix = D.Mixture(sets)
    emit(event="data", train=sum(len(ids) for _, ids, _ in mix.train), val=len(mix.val),
         tokens=sum(ds.total_tokens() for ds, _ in sets), device=device,
         gpu=torch.cuda.get_device_name(0) if device == "cuda" else None)

    if args.init:
        model, extra = load(args.init, device)
    else:
        model = Muse(Config(layers=args.layers, dim=args.dim, heads=args.heads)).to(device)
        extra = {}
    emit(event="model", parameters=model.parameter_count(), fine_tune=bool(args.init))

    decay = [p for n, p in model.named_parameters() if p.dim() >= 2]
    no_decay = [p for n, p in model.named_parameters() if p.dim() < 2]
    opt = torch.optim.AdamW([{"params": decay, "weight_decay": 0.1}, {"params": no_decay, "weight_decay": 0.0}],
                            lr=args.lr, betas=(0.9, 0.95), fused=device == "cuda")
    use_amp = device == "cuda"
    amp_dtype = torch.bfloat16 if use_amp and torch.cuda.is_bf16_supported() else torch.float16

    def lr_at(step):
        if step < args.warmup:
            return args.lr * (step + 1) / args.warmup
        progress = (step - args.warmup) / max(1, args.steps - args.warmup)
        return args.lr * (0.1 + 0.9 * 0.5 * (1 + math.cos(math.pi * min(1.0, progress))))

    def evaluate():
        model.eval()
        total, n = 0.0, 0
        with torch.no_grad(), torch.autocast(device_type="cuda", dtype=amp_dtype, enabled=use_amp):
            for i, x in enumerate(mix.val_batches(args.batch)):
                if i >= 60:
                    break
                total += loss_of(model, x.to(device)).item()
                n += 1
        model.train()
        return total / max(1, n)

    best = float("inf")
    start = time.time()
    tokens_seen = 0
    model.train()
    for step in range(args.steps):
        for g in opt.param_groups:
            g["lr"] = lr_at(step)
        x = mix.batch(args.batch, args.max_tokens).to(device)
        tokens_seen += x.numel()
        with torch.autocast(device_type="cuda", dtype=amp_dtype, enabled=use_amp):
            loss = loss_of(model, x)
        opt.zero_grad(set_to_none=True)
        loss.backward()
        torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
        opt.step()
        if step % 50 == 0:
            elapsed = time.time() - start
            emit(event="progress", step=step, steps=args.steps, loss=round(loss.item(), 4), lr=lr_at(step),
                 tokens_per_second=int(tokens_seen / max(1e-6, elapsed)),
                 eta_seconds=int(elapsed / max(1, step) * (args.steps - step)))
        if (step + 1) % args.eval_every == 0 or step == args.steps - 1:
            val = evaluate()
            emit(event="eval", step=step + 1, val_loss=round(val, 4))
            if val < best:
                best = val
                save(model, os.path.join(args.out, "model.pt"), {**extra, "val_loss": val, "step": step + 1})
    model, _ = load(os.path.join(args.out, "model.pt"), "cpu")
    export_onnx(model, os.path.join(args.out, "model.onnx"))
    info = {"val_loss": best, "steps": args.steps, "parameters": model.parameter_count(),
            "fine_tuned_from": args.init, "data": args.data, "own": args.own, "seconds": int(time.time() - start)}
    with open(os.path.join(args.out, "info.json"), "w") as f:
        json.dump(info, f, indent=2)
    emit(event="done", **info)
    return 0


if __name__ == "__main__":
    sys.exit(main())
