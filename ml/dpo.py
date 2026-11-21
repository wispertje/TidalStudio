"""Learn from your ratings (train by ear): DPO on liked-vs-disliked pairs, plus a little plain training on liked parts.

  python dpo.py --init <model.pt> --ratings <ratings.jsonl> --out <folder> [--epochs 3]

ratings.jsonl lines (written by TidalStudio):
  {"prompt": [...header tokens up to SEP...], "chosen": [...body...], "rejected": [...body...]}   (a pair)
  {"prompt": [...], "chosen": [...]}                                                             (a liked part)
"""
import argparse
import copy
import json
import os
import random
import sys
import time

import torch
import torch.nn.functional as F

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from muse.model import save, load, export_onnx  # noqa: E402


def emit(**kw):
    print(json.dumps(kw), flush=True)


def seq_logp(model, prompt, body, device):
    x = torch.tensor([prompt + body], dtype=torch.long, device=device)
    logits = model(x[:, :-1]).float()
    logp = F.log_softmax(logits, dim=-1)
    target = x[:, 1:]
    token_logp = logp.gather(2, target[:, :, None])[0, :, 0]
    return token_logp[len(prompt) - 1:].sum()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--init", required=True)
    ap.add_argument("--ratings", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--epochs", type=int, default=3)
    ap.add_argument("--beta", type=float, default=0.1)
    ap.add_argument("--lr", type=float, default=2e-5)
    ap.add_argument("--sft", type=float, default=0.2, help="weight of plain training on liked parts")
    args = ap.parse_args()
    device = "cuda" if torch.cuda.is_available() else "cpu"
    os.makedirs(args.out, exist_ok=True)

    items = [json.loads(l) for l in open(args.ratings, encoding="utf-8") if l.strip()]
    pairs = [i for i in items if i.get("rejected")]
    liked = [i for i in items if i.get("chosen")]
    emit(event="data", pairs=len(pairs), liked=len(liked), device=device)
    if not liked:
        emit(event="error", message="no ratings yet")
        return 1

    model, extra = load(args.init, device)
    reference = copy.deepcopy(model).eval()
    for p in reference.parameters():
        p.requires_grad_(False)
    opt = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=0.0)
    model.train()
    steps = args.epochs * max(len(pairs), len(liked))
    step = 0
    start = time.time()
    for epoch in range(args.epochs):
        random.shuffle(pairs)
        random.shuffle(liked)
        for k in range(max(len(pairs), len(liked))):
            loss = torch.zeros((), device=device)
            if pairs:
                p = pairs[k % len(pairs)]
                lc = seq_logp(model, p["prompt"], p["chosen"], device)
                lr_ = seq_logp(model, p["prompt"], p["rejected"], device)
                with torch.no_grad():
                    rc = seq_logp(reference, p["prompt"], p["chosen"], device)
                    rr = seq_logp(reference, p["prompt"], p["rejected"], device)
                loss = loss - F.logsigmoid(args.beta * ((lc - rc) - (lr_ - rr)))
            if liked and args.sft > 0:
                q = liked[k % len(liked)]
                n = max(1, len(q["chosen"]))
                loss = loss - args.sft * seq_logp(model, q["prompt"], q["chosen"], device) / n
            opt.zero_grad(set_to_none=True)
            loss.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), 1.0)
            opt.step()
            if step % 10 == 0:
                emit(event="progress", step=step, steps=steps, loss=round(loss.item(), 4))
            step += 1
    save(model, os.path.join(args.out, "model.pt"), {**extra, "dpo_pairs": len(pairs), "dpo_liked": len(liked)})
    export_onnx(model.to("cpu"), os.path.join(args.out, "model.onnx"))
    info = {"fine_tuned_from": args.init, "pairs": len(pairs), "liked": len(liked), "seconds": int(time.time() - start), "kind": "ratings"}
    with open(os.path.join(args.out, "info.json"), "w") as f:
        json.dump(info, f, indent=2)
    emit(event="done", **info)
    return 0


if __name__ == "__main__":
    sys.exit(main())
