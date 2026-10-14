# Image generation report

Generated 2026-10-14 using the built-in `image_gen.imagegen` tool. Five initial generations and two permitted corrective regenerations: seven total tool calls. No CLI/API fallback was used.

The attached Big Ambitions screenshot was a visual style reference only. Each prompt explicitly forbade copying its HUD, buildings or composition. New image calls used `prompt` and `transparent_background` only; no edit target was passed. `transparent_background=true` for the three car cutouts and `false` for both heroes.

Verification: Python with Pillow 12.3.0, using `/Users/peterhartwieg/Coding_Projects/hart-group/.venv/bin/python`. Reopened each saved deliverable, ran `Image.verify()`, decoded pixels, checked mode/size, asserted requested minimum dimensions and hero aspect ratios, and tested all four corner alphas for cutouts. Also visually reviewed generated images and light-grey composites of every cutout. Visual checks are human/model inspection, not automated OCR or proof of exact camera height/distance.

| File | Mode | Final size | Retries | Cutout alpha checks | Result |
| --- | --- | --- | --- | --- | --- |
| hero-wide.png | RGB | 1920×1080 | 1 | N/A (opaque hero) | PASS |
| hero-square.png | RGB | 1254×1254 | 1 | N/A (opaque hero) | PASS |
| taxi.png | RGBA | 1536×1024 | 0 | [0, 0, 0, 0]; alpha range (0, 254), 884,377 fully transparent pixels | PASS |
| limo.png | RGBA | 1536×1024 | 0 | [0, 0, 0, 0]; alpha range (0, 254), 964,574 fully transparent pixels | PASS |
| own-car.png | RGBA | 1536×1024 | 0 | [0, 0, 0, 0]; alpha range (0, 254), 926,464 fully transparent pixels | PASS |

All files meet the requested final dimensions. The wide hero required a documented upscale because the tool returned 1672×941 despite the requested 2048×1152. All three cutouts arrived with real alpha; no opaque background removal was necessary.

## hero-wide.png

Final file: `../hero-wide.png`. Native selected output: 1672×941. Final PNG: RGB, 1920×1080.

Generations: 2; retries: 1. Retry reason: Initial image had a small trunk emblem/model markings.

Pillow LANCZOS resize from 1672×941 to 1920×1080 to meet the requested minimum and exact 16:9 ratio. This is an upscale, not additional generated detail.

Opaque hero; no cutout-alpha requirement.

Visual review: yellow taxi in right half, heading up/right; distant traffic at red lights; empty road ahead; left 40% dark and calm. No visible lettering, HUD or watermark. New buildings/composition rather than the reference screenshot.

SHA-256: `e46ed4149e80bf5682d5a81b2fa605d14a7164bd2ac0fa90accf7abd76eb4b66`.

### Generation 1 (rejected)

Tool output: RGB, 1672×941. Source: `/Users/peterhartwieg/.codex/generated_images/01a13b77-328c-7660-b0e7-0d82bee3501e/exec-4f8df3e4-aa95-41c5-a74b-a699921c3b2f.png`.

Exact prompt sent to the built-in tool:

```text
Use case: stylized-concept.
Style reference: use the attached Big Ambitions screenshot only to understand the game's visual look, never copy its HUD, buildings, layout or composition. Create a new scene or asset: clean, slightly stylised 3D New York city/game art, Unity HDRP look, soft realistic lighting, readable shapes, saturated but not neon. Cars have rounded, simplified real-world shapes without badges.
Constraints: absolutely no text, lettering, logos, readable number plates, UI, watermark, glow effects or lens flares. Any plates and taxi roof signs are blank.

Asset type: landscape 16:9 hero image, at least 1920x1080; render at 2048x1152 or larger in 16:9.
A yellow New York taxi drives along a wide Manhattan avenue at early dusk, seen from a high three-quarter view behind and above, about 20 m up, so the road ahead and the next junction are visible. Street lights and shop windows are coming on, the sky above the towers is warm orange fading to deep blue. Some traffic cars further ahead wait at a red light; the taxi is the clear subject in the right half of the frame, moving towards the upper right. Keep the left 40 percent of the picture calm and darker (building shade, empty pavement), because a logo goes there later. The road in front of the taxi is empty for about 30 m, because a route line is drawn on it later. Do not draw a logo or route line: preserve that empty space. The left 40 percent must be uncluttered, with no vehicles, people, street furniture or bright focal points.
```

### Generation 2 (selected final)

Tool output: RGB, 1672×941. Source: `/Users/peterhartwieg/.codex/generated_images/01a13b77-328c-7660-b0e7-0d82bee3501e/exec-ca94ccfc-faa6-4c44-b7a8-1bab265e8f6e.png`.

Exact prompt sent to the built-in tool:

```text
Use case: stylized-concept.
Style reference: use the attached Big Ambitions screenshot only to understand the game's visual look, never copy its HUD, buildings, layout or composition. Create a new scene or asset: clean, slightly stylised 3D New York city/game art, Unity HDRP look, soft realistic lighting, readable shapes, saturated but not neon. Cars have rounded, simplified real-world shapes without badges.
Constraints: absolutely no text, lettering, logos, readable number plates, UI, watermark, glow effects or lens flares. Any plates and taxi roof signs are blank.

Asset type: landscape 16:9 hero image, at least 1920x1080; render at 2048x1152 or larger in 16:9.
A yellow New York taxi drives along a wide Manhattan avenue at early dusk, seen from a high three-quarter view behind and above, about 20 m up, so the road ahead and the next junction are visible. Street lights and shop windows are coming on, the sky above the towers is warm orange fading to deep blue. Some traffic cars further ahead wait at a red light; the taxi is the clear subject in the right half of the frame, moving towards the upper right. Keep the left 40 percent of the picture calm and darker (building shade, empty pavement), because a logo goes there later. The road in front of the taxi is empty for about 30 m, because a route line is drawn on it later. Do not draw a logo or route line: preserve that empty space. The left 40 percent must be uncluttered, with no vehicles, people, street furniture or bright focal points.

Retry correction: the previous result introduced a small trunk emblem and model markings. Remove every badge, emblem, model inscription and letter. Storefront awnings and all sign panels must be completely blank and unmarked; taxi trunk and blank roof light have no symbols at all. Use simple stylised 3D game meshes and moderately simplified textures as in Big Ambitions, with soft Unity HDRP illumination; reduce photographic detail. Preserve the specified wide composition, dark empty left 40 percent and empty road ahead. No text anywhere. Requested size is 2048x1152, 16:9.
```

## hero-square.png

Final file: `../hero-square.png`. Native selected output: 1254×1254. Final PNG: RGB, 1254×1254.

Generations: 2; retries: 1. Retry reason: Initial image had car badges/model inscriptions and small storefront lettering.

Copied the selected tool output unchanged; no resizing or background removal.

Opaque hero; no cutout-alpha requirement.

Visual review: yellow taxi lower centre; empty avenue ahead to a red-light junction; tower tops and sky occupy the upper region. No visible lettering, HUD or watermark. Square recomposition with matching dusk palette.

SHA-256: `ac8f6e9569b6a917a074f0e21d1f200f740937f97943408382f93e5e4767cc89`.

### Generation 1 (rejected)

Tool output: RGB, 1254×1254. Source: `/Users/peterhartwieg/.codex/generated_images/01a13b77-328c-7660-b0e7-0d82bee3501e/exec-3240c52c-98c9-4784-8e65-fe3cb3035b45.png`.

Exact prompt sent to the built-in tool:

```text
Use case: stylized-concept.
Style reference: use the attached Big Ambitions screenshot only to understand the game's visual look, never copy its HUD, buildings, layout or composition. Create a new scene or asset: clean, slightly stylised 3D New York city/game art, Unity HDRP look, soft realistic lighting, readable shapes, saturated but not neon. Cars have rounded, simplified real-world shapes without badges.
Constraints: absolutely no text, lettering, logos, readable number plates, UI, watermark, glow effects or lens flares. Any plates and taxi roof signs are blank.

Asset type: square hero image, at least 1024x1024; render square.
A yellow New York taxi drives along a wide Manhattan avenue at early dusk, seen from a high three-quarter view behind and above, about 20 m up. Same scene and lighting as hero-wide: street lights and shop windows coming on, warm orange sky fading to deep blue, some traffic cars further ahead waiting at a red light. Recompose for a square: the taxi lower centre, heading up the avenue towards a junction with a traffic light, the towers framing it. Keep the top 30 percent calm (sky and tower tops), because a logo goes there later. Keep the road ahead of the taxi empty for about 30 m. Do not draw any logo or route line.
```

### Generation 2 (selected final)

Tool output: RGB, 1254×1254. Source: `/Users/peterhartwieg/.codex/generated_images/01a13b77-328c-7660-b0e7-0d82bee3501e/exec-bb304969-09a5-4e90-a6ff-b0168071e1f0.png`.

Exact prompt sent to the built-in tool:

```text
Use case: stylized-concept.
Style reference: use the attached Big Ambitions screenshot only to understand the game's visual look, never copy its HUD, buildings, layout or composition. Create a new scene or asset: clean, slightly stylised 3D New York city/game art, Unity HDRP look, soft realistic lighting, readable shapes, saturated but not neon. Cars have rounded, simplified real-world shapes without badges.
Constraints: absolutely no text, lettering, logos, readable number plates, UI, watermark, glow effects or lens flares. Any plates and taxi roof signs are blank.

Asset type: square hero image, at least 1024x1024; render square.
A yellow New York taxi drives along a wide Manhattan avenue at early dusk, seen from a high three-quarter view behind and above, about 20 m up. Same scene and lighting as hero-wide: street lights and shop windows coming on, warm orange sky fading to deep blue, some traffic cars further ahead waiting at a red light. Recompose for a square: the taxi lower centre, heading up the avenue towards a junction with a traffic light, the towers framing it. Keep the top 30 percent calm (sky and tower tops), because a logo goes there later. Keep the road ahead of the taxi empty for about 30 m. Do not draw any logo or route line.

Retry correction: the previous result introduced vehicle badges and tiny storefront lettering. Remove every badge, emblem, model inscription and letter. Storefront awnings and all sign panels must be completely blank and unmarked; taxi trunk and blank roof light have no symbols at all. Use simple stylised 3D game meshes and moderately simplified textures as in Big Ambitions, with soft Unity HDRP illumination; reduce photographic detail. Preserve the specified square composition and empty road. No text anywhere.
```

## taxi.png

Final file: `../taxi.png`. Native selected output: 1536×1024. Final PNG: RGBA, 1536×1024.

Generations: 1; retries: 0. No retry needed.

Copied the selected tool output unchanged; no resizing or background removal.

Corner alpha (top-left, top-right, bottom-left, bottom-right): `[0, 0, 0, 0]`. Alpha extrema: `(0, 254)`. Fully transparent pixels: `884,377` / `1,572,864`. Alpha was supplied by the tool and preserved; background removal was unnecessary. The tool's maximum alpha is 254 (near-opaque), retained unchanged.

Visual review: yellow taxi sedan, blank roof sign/plate, front three-quarter view from above; full vehicle within canvas. Inspected composited on a light grey background: clean silhouette, no opaque backdrop or visible halo.

SHA-256: `4569239715944976bfb55073b9fbdc37c5e1d69d0c232f1f5747d6b01e2cab00`.

### Generation 1 (selected final)

Tool output: RGBA, 1536×1024. Source: `/Users/peterhartwieg/.codex/generated_images/01a13b77-328c-7660-b0e7-0d82bee3501e/exec-5369f235-c05b-43d1-92ba-9cce033ea7d7.png`.

Exact prompt sent to the built-in tool:

```text
Use case: stylized-concept.
Style reference: use the attached Big Ambitions screenshot only to understand the game's visual look, never copy its HUD, buildings, layout or composition. Create a new scene or asset: clean, slightly stylised 3D New York city/game art, Unity HDRP look, soft realistic lighting, readable shapes, saturated but not neon. Cars have rounded, simplified real-world shapes without badges.
Constraints: absolutely no text, lettering, logos, readable number plates, UI, watermark, glow effects or lens flares. Any plates and taxi roof signs are blank.

Asset type: transparent PNG cutout, landscape, at least 1536x1024.
A yellow New York taxi sedan alone, front three-quarter view from slightly above, wheels turned a little as if driving. Soft studio light from upper left, a soft contact shadow under it is fine. Genuinely transparent background: alpha 0 around the car and in all four corners, no backdrop, no floor or scenery, no baked checkerboard. Show the entire car with generous clear padding on every side. Taxi roof sign must be blank, no text.
```

## limo.png

Final file: `../limo.png`. Native selected output: 1536×1024. Final PNG: RGBA, 1536×1024.

Generations: 1; retries: 0. No retry needed.

Copied the selected tool output unchanged; no resizing or background removal.

Corner alpha (top-left, top-right, bottom-left, bottom-right): `[0, 0, 0, 0]`. Alpha extrema: `(0, 254)`. Fully transparent pixels: `964,574` / `1,572,864`. Alpha was supplied by the tool and preserved; background removal was unnecessary. The tool's maximum alpha is 254 (near-opaque), retained unchanged.

Visual review: black stretch limousine with clearly elongated body, blank plate, front three-quarter view from above; full vehicle within canvas. Inspected composited on a light grey background: clean silhouette, no opaque backdrop or visible halo.

SHA-256: `6945a75c656e9d6f79994fd654ed249bf209873833fc5e424dc75ed47908fd83`.

### Generation 1 (selected final)

Tool output: RGBA, 1536×1024. Source: `/Users/peterhartwieg/.codex/generated_images/01a13b77-328c-7660-b0e7-0d82bee3501e/exec-6320d9d7-0812-40c2-93cf-3910db640cd8.png`.

Exact prompt sent to the built-in tool:

```text
Use case: stylized-concept.
Style reference: use the attached Big Ambitions screenshot only to understand the game's visual look, never copy its HUD, buildings, layout or composition. Create a new scene or asset: clean, slightly stylised 3D New York city/game art, Unity HDRP look, soft realistic lighting, readable shapes, saturated but not neon. Cars have rounded, simplified real-world shapes without badges.
Constraints: absolutely no text, lettering, logos, readable number plates, UI, watermark, glow effects or lens flares. Any plates and taxi roof signs are blank.

Asset type: transparent PNG cutout, landscape, at least 1536x1024.
A black stretch limousine, the private driver's car, alone, front three-quarter view from slightly above, wheels turned a little as if driving. Same view and soft studio light from upper left as taxi.png; a soft contact shadow under it is fine. Clearly elongated stretch limousine body, not an ordinary sedan. Genuinely transparent background: alpha 0 around the car and in all four corners, no backdrop, no floor or scenery, no baked checkerboard. Show the entire car with generous clear padding on every side.
```

## own-car.png

Final file: `../own-car.png`. Native selected output: 1536×1024. Final PNG: RGBA, 1536×1024.

Generations: 1; retries: 0. No retry needed.

Copied the selected tool output unchanged; no resizing or background removal.

Corner alpha (top-left, top-right, bottom-left, bottom-right): `[0, 0, 0, 0]`. Alpha extrema: `(0, 254)`. Fully transparent pixels: `926,464` / `1,572,864`. Alpha was supplied by the tool and preserved; background removal was unnecessary. The tool's maximum alpha is 254 (near-opaque), retained unchanged.

Visual review: deep green compact five-door hatchback, short rear overhang, blank plate, front three-quarter view from above; full vehicle within canvas. Inspected composited on a light grey background: clean silhouette, no opaque backdrop or visible halo.

SHA-256: `95c5a9dc94170e44ecf79aed268b6beecf075d93ae473ce81c2abb891980ca92`.

### Generation 1 (selected final)

Tool output: RGBA, 1536×1024. Source: `/Users/peterhartwieg/.codex/generated_images/01a13b77-328c-7660-b0e7-0d82bee3501e/exec-e80bc2c2-3de4-49c6-8fe3-3111b35a3212.png`.

Exact prompt sent to the built-in tool:

```text
Use case: stylized-concept.
Style reference: use the attached Big Ambitions screenshot only to understand the game's visual look, never copy its HUD, buildings, layout or composition. Create a new scene or asset: clean, slightly stylised 3D New York city/game art, Unity HDRP look, soft realistic lighting, readable shapes, saturated but not neon. Cars have rounded, simplified real-world shapes without badges.
Constraints: absolutely no text, lettering, logos, readable number plates, UI, watermark, glow effects or lens flares. Any plates and taxi roof signs are blank.

Asset type: transparent PNG cutout, landscape, at least 1536x1024.
A modern compact family hatchback in deep green (close to #00703A), alone, front three-quarter view from slightly above, wheels turned a little as if driving. Same view and soft studio light from upper left as taxi.png; a soft contact shadow under it is fine. This represents your own car. Clearly a compact hatchback with a short rear overhang, not a sedan or SUV. Genuinely transparent background: alpha 0 around the car and in all four corners, no backdrop, no floor or scenery, no baked checkerboard. Show the entire car with generous clear padding on every side.
```
