# Depth from a photo: planned, not built

The photo importer can take a depth map: a greyscale picture lined up with the photo, lighter nearer, which pushes
each voxel in or out. This note is the plan for making that map from the photo itself, on the player's machine. The
hook for it is in place (`IDepthEstimator` and `DepthEstimators` in
`src/ToyCity.ClientModel/Voxels/Assets/DepthEstimate.cs`). The editor's **Depth from the photo** button is there but
off until an estimator is installed.

## The model

**Depth Anything V2 Small** (Apache-2.0).

- It's about 25M parameters, roughly 100 MB as a 32-bit ONNX file and half that at 16-bit.
- It should take about a second a photo on most graphics cards and a few seconds on a processor.
- Its output is relative inverse depth, where bigger is nearer, which is what `DepthEstimators.ToDepthMap` expects by
  default.

Not these:

- **Depth Anything V2 Base and Large** are licensed CC-BY-NC-4.0, non-commercial only, so they can't ship in the game.
- **Marigold** is built on Stable Diffusion. It needs around 4–8 GB of graphics memory and many denoising steps a
  photo, which is too heavy for most players' machines.

## How it would run

- **Runtime.** Use `Microsoft.ML.OnnxRuntime` from the Godot client (C#). On Windows, use the DirectML build so it
  runs on any DirectX 12 graphics card, and fall back to the processor. On Linux and macOS use the processor, which
  is still a few seconds a photo.
- **Download.** Fetch the model on first use, not in the game download, into `user://models/depth-anything-v2-small.onnx`.
  - Check it against a pinned SHA-256 before loading.
  - Show the size and ask first ("Download the depth model, about 100 MB?").
  - Keep a link to the licence beside it.
- **Input.**
  - Scale the photo so its short side is 518 px and both sides are multiples of 14. This is the model's patch size.
  - Normalise RGB with the ImageNet mean (0.485, 0.456, 0.406) and deviation (0.229, 0.224, 0.225).
  - The tensor is NCHW float32.
- **Output.** A `[1, H, W]` float map. `DepthEstimators.ToDepthMap(values, W, H, photo.Width, photo.Height)` turns
  it into a depth map the photo's size, stretching the 2nd to 98th percentile to black and white.
- **Editor.** Run it off the main thread with a "Working out depth…" note, since long actions need immediate
  feedback. Then set the importer's depth map, exactly as if the player had loaded one.

## What's left to do

1. Add the ONNX Runtime package to `clients/godot`, and an `OnnxDepthEstimator : IDepthEstimator` in the Godot
   client. Keep ClientModel free of the dependency.
2. Add the download with a progress bar, the hash check and the licence note.
3. Set `DepthEstimators.Current` when the model file is present.
4. Test it on a few real building photos and compare against a hand-made depth map.
