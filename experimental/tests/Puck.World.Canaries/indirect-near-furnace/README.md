# Near replacement conserves finite furnace energy

The prepared closed shell is reused at its original dimensions and gray material.
Only Emission and Feedback are enabled, with four feedback bounces. Puck's
HexColor conversion maps #808080 directly to float32 128/255; no sRGB transfer
is applied. Thus rho=0.501960813999176, e=rho*0.125, and normalized incoming is
e*(1+rho+rho²+rho³+rho⁴)=0.121969453317935. The actual captured CPU reference
must hold this series to its six-decimal printed precision. The Near incoming
answer must agree within 20% and retain emission separately from feedback.
Adding the cache answer instead of replacing it would double the energy and
fail the same bound.

The companion changes only the shell's emissive strength to zero. Both incoming
categories and the independent reference must be exactly black while the same
finite depth, provenance and resolved Near outcome hold. A Near hit or certified
directional continuation is accepted; an unattempted or unresolved Near cannot
claim the physical observation. The source uses the current fenced solve and
shared picker. The actual chosen pixel, GPU values and native additive mutant
remain unqualified until the serialized backend run.
