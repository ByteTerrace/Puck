# Finite diffuse furnace

This canary owns the shared [base world](base.puck), which supplies offscreen
presentation and neutral rendering to the [bleed](../gi-bleed/README.md),
[sealed-room](../gi-sealed/README.md), [indirect-off](../indirect-off/README.md)
and [sky](../indirect-sky/README.md) canaries. Each canary owns its geometry,
source controls, independent observation and opposite case. Cache readiness
uses the actual fenced `world.wait indirect` predicate; a fixed warm-up duration
does not substitute for it.

A white closed sphere emits 0.125 into a unit-reflectance interior. With two
feedback sweeps, the normalized incoming value is 0.375: emission 0.125 plus
feedback 0.25. The companion disables feedback and requires one sweep and
0.125. No explicit light, screen, sky, artistic fill, AO or temporal history
contributes.

Both observations come from the current fenced GPU pick and its independently
cast CPU reference. The source categories stay separate. The GPU tolerances
cover two low-precision radiance quantization steps; the reference holds the
closed form to its printed precision. A queued reset must advance the cache
epoch, solve again, and recover identical center-region pixels.

This source fixture awaits actual Vulkan and DirectX qualification through
the native canary command. It does not replace the delayed-fence cold-capture
law or device normalization discriminator.
