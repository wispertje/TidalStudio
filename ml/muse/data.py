"""Token sequences written by TidalStudio (Dataset.Writer): tokens.bin (uint16) + index.bin (int64 offsets)."""
import os
import random

import numpy as np
import torch


class Sequences:
    def __init__(self, folder):
        self.folder = folder
        self.tokens = np.fromfile(os.path.join(folder, "tokens.bin"), dtype=np.uint16)
        self.index = np.fromfile(os.path.join(folder, "index.bin"), dtype=np.int64)

    def __len__(self):
        return max(0, len(self.index) - 1)

    def get(self, i):
        return self.tokens[self.index[i]:self.index[i + 1]].astype(np.int64)

    def total_tokens(self):
        return int(self.index[-1]) if len(self.index) else 0


def exists(folder):
    return folder and os.path.exists(os.path.join(folder, "tokens.bin")) and os.path.exists(os.path.join(folder, "index.bin"))


class Mixture:
    """Samples sequences from several datasets with weights (e.g. your own music more often than MIDI)."""

    def __init__(self, sets_and_weights, val_fraction=0.02, seed=1):
        rng = random.Random(seed)
        self.train, self.val = [], []
        for ds, weight in sets_and_weights:
            ids = list(range(len(ds)))
            rng.shuffle(ids)
            n_val = max(1, int(len(ids) * val_fraction)) if len(ids) > 20 else 0
            self.val += [(ds, i) for i in ids[:n_val]]
            self.train.append((ds, ids[n_val:], weight))
        self.weights = [w * len(ids) for _, ids, w in self.train]
        self.rng = rng

    def sample(self):
        ds, ids, _ = self.rng.choices(self.train, weights=self.weights)[0]
        return ds.get(self.rng.choice(ids))

    def batch(self, size, max_tokens=None):
        seqs = [self.sample() for _ in range(size)]
        if max_tokens:
            # Keep the padded batch under a token budget.
            seqs.sort(key=len)
            while len(seqs) > 1 and len(seqs) * len(seqs[-1]) > max_tokens:
                seqs.pop()
        return collate(seqs)

    def val_batches(self, size):
        for i in range(0, len(self.val), size):
            yield collate([ds.get(j) for ds, j in self.val[i:i + size]])


def collate(seqs):
    t = max(len(s) for s in seqs)
    x = np.zeros((len(seqs), t), dtype=np.int64)
    for i, s in enumerate(seqs):
        x[i, :len(s)] = s
    return torch.from_numpy(x)
