#ifndef SKY_KINDS_PANEL_HLSLI
#define SKY_KINDS_PANEL_HLSLI
#include "../../shade/sdf-sky-panel.hlsli"
float4 sdfSkyPanelLayer(SdfSkyPanel panel, SdfSkyLayer layer, SdfSkySample sample) {
    if (panel.Intensity <= 0.0) {
        return 0.0.xxxx;
    }
    sdfCountSky(layer.Detail, 0u, 0u, 1u, 0u, 0u);
    return sdfSkyPanelValue(panel, sample.local, 0.0);
}
#endif
