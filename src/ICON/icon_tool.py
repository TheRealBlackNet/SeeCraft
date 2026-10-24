#!/usr/bin/env python3
"""
Icon-Tool
  scale : NAME-16x16.ico -> NAME-32x32.ico, NAME-64x64.ico, NAME-128x128.ico
		  (pixelperfekt, Nearest Neighbor)
  merge : NAME-16x16/32x32/64x64/128x128.ico -> NAME.ico (wird überschrieben)
  all   : scale + merge

Aufruf:
  python icon_tool.py all   "C:\\Pfad\\zum\\Ordner"
  python icon_tool.py scale "C:\\Pfad\\zum\\Ordner"
  python icon_tool.py merge "C:\\Pfad\\zum\\Ordner"
"""
import argparse
import io
import os
import re
import struct
import sys
from pathlib import Path

from PIL import Image

SIZES = [16, 32, 64, 128]
SIZE_SUFFIX = re.compile(r"-\d+x\d+$")


def size_name(base: str, s: int) -> str:
	return f"{base}-{s}x{s}.ico"


def find_bases(folder: Path) -> list[str]:
	"""Alle Basisnamen (ohne Größenangabe) im Ordner."""
	bases = set()
	for f in folder.glob("*.ico"):
		bases.add(SIZE_SUFFIX.sub("", f.stem))
	return sorted(bases)


def load_frame(path: Path, size: int) -> Image.Image:
	"""Lädt aus einer ICO den Frame der gewünschten Größe (sonst den größten)."""
	with Image.open(path) as im:
		sizes = sorted(im.ico.sizes()) if hasattr(im, "ico") else [im.size]
		chosen = (size, size) if (size, size) in sizes else sizes[-1]
		if hasattr(im, "ico"):
			frame = im.ico.getimage(chosen)
		else:
			frame = im
		return frame.convert("RGBA").copy()


def write_ico(path: Path, images: list[Image.Image]) -> None:
	"""Schreibt eine ICO mit genau den übergebenen Bildern (PNG-komprimiert)."""
	blobs = []
	for im in images:
		buf = io.BytesIO()
		im.convert("RGBA").save(buf, format="PNG")
		blobs.append((im.width, im.height, buf.getvalue()))

	header = struct.pack("<HHH", 0, 1, len(blobs))
	offset = 6 + 16 * len(blobs)
	entries, data = b"", b""
	for w, h, blob in blobs:
		entries += struct.pack(
			"<BBBBHHII",
			0 if w >= 256 else w,
			0 if h >= 256 else h,
			0, 0, 1, 32, len(blob), offset,
		)
		data += blob
		offset += len(blob)

	tmp = path.with_suffix(".tmp")
	tmp.write_bytes(header + entries + data)
	os.replace(tmp, path)  # überschreibt vorhandene Datei sicher


def do_scale(folder: Path, force: bool) -> None:
	for base in find_bases(folder):
		p16 = folder / size_name(base, 16)
		if not p16.exists():
			src = folder / f"{base}.ico"
			if not src.exists():
				print(f"[übersprungen] {base}: keine 16x16-Quelle")
				continue
			write_ico(p16, [load_frame(src, 16)])
			print(f"[erstellt] {p16.name} (aus {src.name})")

		img16 = load_frame(p16, 16)
		for s in SIZES[1:]:
			target = folder / size_name(base, s)
			if target.exists() and not force:
				print(f"[vorhanden] {target.name}")
				continue
			big = img16.resize((s, s), Image.NEAREST)
			write_ico(target, [big])
			print(f"[erstellt] {target.name}")


def do_merge(folder: Path) -> None:
	for base in find_bases(folder):
		frames = []
		for s in SIZES:
			p = folder / size_name(base, s)
			if p.exists():
				frames.append(load_frame(p, s))
			else:
				print(f"[Warnung] {p.name} fehlt")
		if not frames:
			continue
		out = folder / f"{base}.ico"
		write_ico(out, frames)
		print(f"[zusammengeführt] {out.name} ({len(frames)} Größen)")


def main() -> int:
	ap = argparse.ArgumentParser(description="ICO skalieren und zusammenführen")
	ap.add_argument("mode", choices=["scale", "merge", "all"])
	ap.add_argument("folder", type=Path)
	ap.add_argument("--force", action="store_true",
					help="vorhandene NAME-32x32 usw. beim Skalieren überschreiben")
	args = ap.parse_args()

	if not args.folder.is_dir():
		print("Ordner nicht gefunden:", args.folder)
		return 1
	if args.mode in ("scale", "all"):
		do_scale(args.folder, args.force)
	if args.mode in ("merge", "all"):
		do_merge(args.folder)
	return 0


if __name__ == "__main__":
	sys.exit(main())