// Copyright (c) 2026 Yize Wu
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace Gsplat
{
    [CreateAssetMenu(menuName = "Gsplat/Gsplat Material")]
    public class GsplatMaterial : ScriptableObject
    {
        public Material DefaultMaterial;
        private Material[][] m_material;
        private Material[][] m_omniMaterial;

        public void Reset()
        {
            m_material = null;
            m_omniMaterial = null;
        }

        public Material[][] Materials // materials generated with SH bands from 0 to 3 and custom renderOrders
        {
            get
            {
                if (m_material != null && m_material[0][0] != null)
                    return m_material;

                m_material = CreateMaterials(false);
                return m_material;
            }
        }

        public Material[][] OmniMaterials
        {
            get
            {
                if (m_omniMaterial != null && m_omniMaterial[0][0] != null)
                    return m_omniMaterial;

                m_omniMaterial = CreateMaterials(true);
                return m_omniMaterial;
            }
        }

        Material[][] CreateMaterials(bool omni)
        {
            var materials = new Material[4][];
            for (var i = 0; i < 4; ++i)
            {
                materials[i] = new Material[GsplatSettings.Instance.MaxRenderOrder];
                for (var j = 0; j < GsplatSettings.Instance.MaxRenderOrder; ++j)
                {
                    var material = new Material(DefaultMaterial);
                    // Perspective GSplat uses Unity instancing for both splat batches and XR eye routing.
                    // OmniERP itself remains a mono offscreen pass, but sharing this flag is harmless.
                    material.enableInstancing = true;
                    material.DisableKeyword($"SH_BANDS_0");
                    material.DisableKeyword($"SH_BANDS_1");
                    material.DisableKeyword($"SH_BANDS_2");
                    material.DisableKeyword($"SH_BANDS_3");
                    material.EnableKeyword($"SH_BANDS_{i}");
                    material.SetShaderPassEnabled("Perspective", !omni);
                    material.SetShaderPassEnabled("OmniERP", omni);
                    material.renderQueue = 3000 + j;
                    materials[i][j] = material;
                }
            }

            return materials;
        }

        public ComputeShader CalcDepthShader;
        public ComputeShader InitOrderShader;
    }
}
