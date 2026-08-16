"""Generates the parity pattern-world corpus in this directory.

Skeleton: dive.world.json with water removed and the grounded kit stack from
play.world.json, no peers, and per-pattern creations/placements. Creation
hashes come from the validator itself: generate with a changed pattern, boot
the world, and the refusal names the canonical sha256; pass it back via
--hashes id=hex,... (see README.md).
"""
import argparse
import copy
import hashlib
import json
import math
import pathlib
import struct

REPO = pathlib.Path(__file__).resolve().parents[3]
SOURCE = REPO / "src/Puck.World/Assets/worlds/dive.world.json"
PLAY = REPO / "src/Puck.World/Assets/worlds/play.world.json"
TARGET = pathlib.Path(__file__).resolve().parent

ZERO_HASH = "0" * 64


def shape(sid, name, stype, position, scale, material, blend=None, smooth=0, rotation=None, mirror=False, twist=0, onion=0, bend=0, dilate=0):
    return {
        "id": sid,
        "name": name,
        "type": stype,
        "position": {"x": position[0], "y": position[1], "z": position[2]},
        "rotation": rotation or {"isIdentity": True, "x": 0, "y": 0, "z": 0, "w": 1},
        "scale": {"x": scale[0], "y": scale[1], "z": scale[2]},
        "material": material,
        "blend": blend or "Union",
        "smooth": smooth,
        "group": 0,
        "mirror": mirror,
        "twist": twist,
        "onion": onion,
        "bend": bend,
        "dilate": dilate,
    }


def yaw_rotation(degrees):
    half = math.radians(degrees) / 2.0
    return {"isIdentity": False, "x": 0, "y": round(math.sin(half), 7), "z": 0, "w": round(math.cos(half), 7)}


def material(albedo, emissive=0, specular=0.1, shininess=8):
    return {"albedo": {"x": albedo[0], "y": albedo[1], "z": albedo[2]}, "emissive": emissive, "specular": specular, "shininess": shininess}


# Gradients are where benign cross-backend codegen noise clusters: smooth-blend
# seams, curved normals, and broad specular falloff, with no hard edges.
GRADIENT = {
    "palette": [
        material((0.18, 0.19, 0.22), specular=0.05, shininess=4),
        material((0.55, 0.25, 0.20), specular=0.40, shininess=24),
        material((0.20, 0.30, 0.55), specular=0.60, shininess=48),
        material((0.25, 0.50, 0.30), specular=0.20, shininess=12),
    ],
    "shapes": [
        shape(0, "floor", "Plane", (0, 0, 0), (1, 1, 1), 0),
        shape(1, "dome-red", "Sphere", (-3.2, 2.2, 0), (2.6, 2.6, 2.6), 1, blend="SmoothUnion", smooth=0.5),
        shape(2, "dome-blue", "Ellipsoid", (3.2, 1.8, -0.6), (2.9, 1.7, 2.3), 2, blend="SmoothUnion", smooth=0.5),
        shape(3, "ring", "Torus", (0, 1.1, -3.4), (1.9, 1.9, 1.9), 3, blend="SmoothUnion", smooth=0.4),
        shape(4, "horn", "RoundCone", (0, 1.6, 3.0), (1.4, 1.4, 1.4), 1, blend="SmoothUnion", smooth=0.3),
    ],
}

# Hard high-contrast edges: near-black ground, white checker boxes, one yawed
# box for angled silhouettes, a thin distant sliver, and an emissive bar.
EDGES = {
    "palette": [
        material((0.03, 0.03, 0.035), specular=0.02, shininess=2),
        material((0.95, 0.95, 0.95), specular=0.10, shininess=8),
        material((0.90, 0.55, 0.15), emissive=0.8, specular=0.05, shininess=4),
    ],
    "shapes": [
        shape(0, "floor", "Plane", (0, 0, 0), (1, 1, 1), 0),
        *[
            shape(1 + i, f"checker-{i}", "Box", (-4.5 + 3.0 * (i % 4), 0.75, -1.5 + 3.0 * (i // 4)), (1.5, 1.5, 1.5), 1)
            for i in range(8)
            if ((i % 4) + (i // 4)) % 2 == 0
        ],
        shape(9, "yawed", "Box", (0, 0.9, 3.6), (1.8, 1.8, 1.8), 1, rotation=yaw_rotation(45)),
        shape(10, "sliver", "Box", (0, 3.4, -6.0), (10.0, 0.12, 0.12), 1),
        shape(11, "beacon", "Box", (-6.4, 1.8, 0), (0.35, 3.6, 0.35), 2),
    ],
}

# The shape-modifier stress: twist, bend, onion, dilate, and mirror all in one
# frame, so the transform math on both backends is exercised beyond identity.
MODIFIERS = {
    "palette": [
        material((0.20, 0.21, 0.24), specular=0.08, shininess=6),
        material((0.60, 0.45, 0.20), specular=0.30, shininess=20),
        material((0.30, 0.55, 0.60), specular=0.45, shininess=32),
        material((0.55, 0.30, 0.55), specular=0.25, shininess=16),
    ],
    "shapes": [
        shape(0, "floor", "Plane", (0, 0, 0), (1, 1, 1), 0),
        shape(1, "twisted", "Box", (-3.4, 2.0, 0), (1.2, 2.0, 1.2), 1, twist=2.0),
        shape(2, "bent", "Box", (3.4, 1.9, -0.4), (1.1, 1.9, 1.1), 2, bend=0.6),
        shape(3, "shell", "Sphere", (0, 1.7, -3.2), (1.7, 1.7, 1.7), 3, onion=0.15),
        shape(4, "dilated", "Torus", (0, 1.0, 3.2), (1.4, 1.4, 1.4), 1, dilate=0.25),
        shape(5, "mirrored", "RoundCone", (2.2, 1.4, 2.0), (0.9, 0.9, 0.9), 2, mirror=True),
    ],
}

# The glyph tiers, both at once: marched Glyph geometry (an embossed centered run
# with wrap/tracking/line-spacing, plus an engraved run) on a backdrop slab, and
# the dense decal tier on a text-source screen sampling the same packed atlas.
GLYPHS = {
    "palette": [
        material((0.16, 0.17, 0.20), specular=0.05, shininess=4),
        material((0.32, 0.30, 0.28), specular=0.15, shininess=10),
        material((0.92, 0.78, 0.30), specular=0.35, shininess=24),
        material((0.20, 0.45, 0.60), specular=0.30, shininess=20),
    ],
    "shapes": [
        shape(0, "floor", "Plane", (0, 0, 0), (1, 1, 1), 0),
        shape(1, "slab", "Box", (0, 1.9, 0.6), (7.0, 3.4, 0.6), 1),
    ],
    "textRuns": [
        {
            "text": "PUCK PARITY",
            # z = the slab's front face, so the glyph slab straddles the surface (never coplanar, never floating).
            "position": {"x": 0, "y": 2.7, "z": 0.9},
            "rotation": {"isIdentity": True, "x": 0, "y": 0, "z": 0, "w": 1},
            "emHeight": 0.6,
            "depth": 0.06,
            "mode": "emboss",
            "material": 2,
            "maxWidth": 2.4,
            "align": "center",
            "tracking": 0.04,
            "lineSpacing": 1.1,
        },
        {
            "text": "ENGRAVED",
            "position": {"x": 0, "y": 0.9, "z": 0.9},
            "rotation": {"isIdentity": True, "x": 0, "y": 0, "z": 0, "w": 1},
            "emHeight": 0.45,
            "depth": 0.05,
            "mode": "engrave",
            "material": 3,
        },
    ],
    "text": True,
    "screens": [
        {
            "index": 0,
            "origin": [-4.6, 1.7, 4.4],
            "right": [1, 0, 0],
            "up": [0, 1, 0],
            "halfWidth": 1.7,
            "halfHeight": 1.1,
            "halfDepth": 0.12,
            "round": 0.05,
            "source": {
                "$type": "text",
                "lines": ["DECAL TIER", "ABCDEFGHIJKLM", "0123456789", "THE QUICK FOX"],
                "foreground": "#FFD24A",
                "background": "#101018",
            },
            "route": {"engageable": False, "engageRadius": 0, "autoInsert": False},
        }
    ],
}

PATTERNS = {"parity-gradient": GRADIENT, "parity-edges": EDGES, "parity-modifiers": MODIFIERS, "parity-glyphs": GLYPHS}

FONT_SOURCE = "fonts/JetBrainsMono-Regular.ttf"


def font_hash():
    # AssetContentHash: sha256-64/{16 lowercase hex} = the digest's first 8 bytes read little-endian.
    digest = hashlib.sha256((TARGET / FONT_SOURCE).read_bytes()).digest()
    return f"sha256-64/{struct.unpack('<Q', digest[:8])[0]:016x}"


def build(name, pattern, hashes):
    world = copy.deepcopy(json.loads(SOURCE.read_text(encoding="utf-8")))
    play = json.loads(PLAY.read_text(encoding="utf-8"))
    world.pop("water")
    # The grounded kit stack comes from play wholesale so the seat avatar
    # stands at rest with no water dependency; the wander producer is inert at
    # capacity 4 (seats only, no peers).
    for section in ("channels", "bodyMotionPrograms", "kits", "defaultSeatKit", "bindingOverlays"):
        world[section] = play[section]
    world["$schema"] = "../../../src/Puck.World/Assets/worlds/puck.world.def.v1.schema.json"
    world["documentId"] = name
    world["spawnPoints"] = [
        {"id": f"seat-{i + 1}", "position": [(3 * i), 0, 10]} for i in range(4)
    ]
    world["population"]["capacity"] = 4
    world["population"]["networkPlayers"] = 0
    world["population"]["distribution"]["region"]["sampleCount"] = 1
    world["cameras"] = []
    world["references"] = []
    world["destinations"] = []
    world["views"]["seatRig"] = play["views"]["seatRig"]
    world["views"]["seatControl"] = play["views"]["seatControl"]
    document = {
        "schema": "puck.creation.v1",
        "name": name,
        "intent": "Object",
        "bakeStyle": "classic",
        "palette": pattern["palette"],
        "shapes": pattern["shapes"],
        "frames": None,
        "chains": None,
        "cameras": None,
        "behavior": None,
    }
    if "textRuns" in pattern:
        document["textRuns"] = pattern["textRuns"]
    if pattern.get("text"):
        world["text"] = {
            "defaultFont": "body",
            "fonts": [
                {
                    "name": "body",
                    "source": FONT_SOURCE,
                    "hash": font_hash(),
                    "codePointRanges": ["U+0020-U+007E"],
                    "pixelSize": 48,
                    "distanceRange": 8,
                }
            ],
        }
    if "screens" in pattern:
        world["screens"] = pattern["screens"]
    world["creations"] = [
        {
            "id": name,
            "document": document,
            "hash": hashes.get(name, ZERO_HASH),
        }
    ]
    world["placements"] = [
        {
            "id": name,
            "creationId": name,
            "position": [0, 0, 3],
            "yawDegrees": 0,
            "scale": 1.25,
            "distribution": None,
            "mirror": None,
            "solid": {"margin": 0},
        }
    ]
    return world


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--hashes", default="", help="comma-separated id=hex64 overrides from validator refusals")
    args = parser.parse_args()
    hashes = dict(pair.split("=", 1) for pair in args.hashes.split(",") if pair)
    TARGET.mkdir(parents=True, exist_ok=True)
    for name, pattern in PATTERNS.items():
        path = TARGET / f"{name}.world.json"
        path.write_text(json.dumps(build(name, pattern, hashes), indent=2) + "\n", encoding="utf-8", newline="\n")
        print(f"wrote {path}")


if __name__ == "__main__":
    main()
