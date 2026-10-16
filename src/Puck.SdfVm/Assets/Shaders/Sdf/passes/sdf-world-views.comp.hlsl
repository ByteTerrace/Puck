// The views pass: shades each pixel's visibility record into the view's lit image (sdf-views-stage.hlsli) over the
// pixel passes' common head and entry point. Every hit is a mesh pixel, whose material resolves from its record and its
// draw, so the kernel calls no field query and the interpreter the world modules declare compiles out of it.
#include "sdf-pixel-pass.hlsli"
#include "sdf-views-stage.hlsli"
#include "sdf-pixel-entry.hlsli"
