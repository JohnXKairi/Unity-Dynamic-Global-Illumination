/** 
 * Unity script for implementing fake GI effects, based on the article:
 * G. Papaioannou, Approximate Dynamic Global Illumination for VR, submitted to Springer Virtual Reality
 * 
 * Author: Georgios Papaioannou
 * 
 * Copyright 2024 Georgios Papaioannou
 * 
 * MIT License
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the  Software ), to deal 
 * in the Software without restriction, including without limitation the rights 
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies 
 * of the Software, and to permit persons to whom the Software is furnished to do so, 
 * subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED  AS IS , WITHOUT WARRANTY OF ANY KIND, EXPRESS OR 
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS 
 * FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
 * COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER 
 * IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION 
 * WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 * 
 * 
 * How to use this script
 * 
 * 1) Attach the script to one light source. It can be used with
 *    multiple light sources, only in ray casting mode (use_raycasting = true).
 *    
 * 2) Create 1 or more empty game objects (groups) named VPLS and add 
 *    point or spot light sources that represent Virtual Point Lights (VPLs).
 *    The name and the active state of these light sources is irrelevant. 
 *    Adjust their color and pose to match a representative position, 
 *    orientation and color of a reflective surface. Intensity is overriden.
 *    VLPS groups can be stationary or attached to any GameObject.
 *    
 * 3) Optionally, you can define one or more GameObjects named BLOCKERS, which 
 *    can contain (among other things) any number of light sources. These light
 *    sources represent spherical light suppression blobs. Only the position 
 *    and range parameters are relevant. All other light parameters are 
 *    disregarded. Blockers attenuate the contribution of a light source to 
 *    a VPL, according to  the distance of the line from the VPL to the source, 
 *    if the latter crosses the sphere of the blocker defined by the range 
 *    parameter.
 * 
 * 4) Spotlights can use the ray casting mode. With this, a temporary VPL 
 *    is generated at the intersection of the light's axis with the scene 
 *    (see next) and its reflectance attributes are interpolated from the 
 *    declared VPLs. It provides more accurate position for the bounce light 
 *    and can save the trouble of setting up blockers. On the other hand,
 *    it requires collision detection with the scene. Consider using few, 
 *    approximate colliders for better performance. 
 *    
 *    Script options
 *    
 *    use_raycasting: Enable or disable ray casting. Default is false.
 *    
 *    secondary_bounce: Enable approximate secondary bounce light. Default 
 *    is false.
 *    
 *    use_indirect_shadows: Enable shadow maps for VPLs. Default is false. 
 *    Warning, this can have a drastic impact on performance.
 *    
 *    automatic_weights: Compute the area-based weights of the VPLs that 
 *    correspond to their "importance" in the computation of the indirect lighting,
 *    automatically, amortized across N^2 frames, where N is the number of the VPLs.
 *    This means that VPL importance will be gradually updated to match the VPL spacing
 *    as the VPLs move within the scene. Default is false, in which case, all VPLs have 
 *    the same weight. 
 *    
 *    distance_scale: It is the divisor to adjust units to meters. It adjust the 
 *    reflected light brightness, due to distance attenuaton. If geometry is in 
 *    meters, set the  distance scale to 1 (default). If, for example, distances 
 *    are in feet, set distance scale to ~3. If units are in dm, set scale to 10 
 *    and so on.
 *
 *    avg_refl: Average albedo of the surfaces to use for the secondary bounce, 
 *    if enabled. Default value is 0.4.
 *
 *    avg_secondary_distance: is the distance to place the secondary bounce phantom
 *    VPL away from the cluster of contributing static VPLs
 *
 *    brdf_cookie: A light cookie to use for the VPLs to modulate their angular
 *    reflectance response. It is best to use one, for a smooth light gradient. 
 *    The script comes with a symmetrical cookie, whicj works well in most cases.
 *    
 *    For more details about the operation of the method, please see the paper.
 */

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class FakeGI : MonoBehaviour
{
    GameObject dynamicVPLParent;
    public float boost2ndIntensity = 1;
    public float intensityBoost=1;
    public int numOfSecondaryVPLs = 10;
    [SerializeField] private LayerMask layerMask = ~0;
    public float maxDistance = 10.0f;
    public float radius = 0.5f;
    public Light vplPrefab; 
    List<Light> dynamicVPLList = new List<Light>(); 
    List<Light> secondaryVPLs = new List<Light>();
    public int numberOfDynamicVPLs = 1; 
    public bool use_raycasting = false;
    public bool secondary_bounce = false;
    public bool use_indirect_shadows = false;
    public bool automatic_weights = false;
    public float distance_scale = 1.0f;
    public float avg_refl = 0.4f; // average environment reflectance
    public float avg_secondary_distance = 1.0f; // distance to place 
    // the phantom secondary bounce VPL
    public Texture brdf_cookie = null;

    protected List<Light> lights = new List<Light>();
    protected List<Color> reflectance = new List<Color>();
    protected List<float> weights = new List<float>();
    protected List<Light> blockers = new List<Light>();
    protected bool is_directional = false;
    protected bool is_spot = false;

    // smoothing parameters
    bool smooth = true;
    protected Vector3 old_vpl_pos;
    protected Vector3 old_vpl_normal;

    Light source;

    Light dynamic_vpl_secondary;

    static int k = 0;
    static float d_min = 100000.0f;

    protected void UpdateWeightsAmortized()
    {
        if (lights.Count == 1)
        {
            weights[0] = 1.0f;
            return;
        }

        int current = k % lights.Count;
        int other = k / lights.Count;

        // completed one cycle, reset minimum distance;
        if (other == 0)
            d_min = 100000.0f;

        // same VPL, skip
        if (current == other)
        {
            k = (k + 1) % (lights.Count * lights.Count);
            return;
        }

        Vector3 v = lights[current].transform.position - lights[other].transform.position;
        float d = Vector3.Dot(v,v);
        if (d < d_min)
            d_min = d;

        // iterated over all other VPLs, time to update the weight
        if (other == lights.Count -1)
        {
            weights[current] = d_min;
        } 

        k = (k + 1) % (lights.Count * lights.Count);
    }

    protected void GetAllBlockers()
    {
        // search for all blocker groups in the scene, not just one.
        foreach (GameObject group in Resources.FindObjectsOfTypeAll(typeof(GameObject)) as GameObject[])
        {
            if (group.name != "BLOCKERS")
                continue;

            // fetch all children and keep only lights
            for (int i = 0; i < group.transform.childCount; i++)
            {
                Light l = group.transform.GetChild(i).gameObject.GetComponent<Light>();
                if (l != null)
                {
                    l.enabled = false;
                    blockers.Add(l);
                }
            }
        } // foreach object
    }

    protected void GetAllVPLs()
    {
        // search for all VPLS groups in the scene, not just one.
        foreach (GameObject group in Resources.FindObjectsOfTypeAll(typeof(GameObject)) as GameObject[])
        {
            if (group.name != "VPLS" || !group.activeInHierarchy)
                continue;

            // fetch all children and keep only lights
            for (int i = 0; i < group.transform.childCount; i++)
            {
                Light l = group.transform.GetChild(i).gameObject.GetComponent<Light>();

                if (l == null)
                    continue;

                // set up emission characteristics of VPLs
                if (l.type == LightType.Spot)
                {
                    l.innerSpotAngle = 0;
                    l.spotAngle = 170;
                    l.range = source.range;
                }

                // use the predefined intensity as area weighting factor
                weights.Add(l.intensity);

                // by default, disable all VPLs
                l.intensity = 0.0f;
                l.enabled = false;
                lights.Add(l);
                reflectance.Add(l.color);

                // set up indirect shadows (of any)
                if (use_indirect_shadows)
                {
                    l.shadows = LightShadows.Soft;
                    l.shadowCustomResolution = 32;
                }
                else
                    l.shadows = LightShadows.None;
            } // for VPLs
        } // foreach object
    }

    protected float PointToSegmentDistanceSquared(Vector3 q, Vector3 x0, Vector3 x1)
    {
        Vector3 dir = x1 - x0;
        float dist = 0.0f;
        Vector3 dir_norm = Vector3.Normalize(dir);
        float lq = Vector3.Dot(dir_norm, q - x0);
        if (lq < 0.0f)
        {
            Vector3 e = q - x0;
            dist = Vector3.Dot(e, e);
        }
        else if (lq > dir.magnitude)
        {
            Vector3 e = q - x1;
            dist = Vector3.Dot(e, e);
        }
        else
        {
            Vector3 o = dir_norm * lq + x0 - q;
            dist = Vector3.Dot(o, o);
        }
        return dist;
    }

    void Start()
    {
        InitializeSecondaryVPLs(numOfSecondaryVPLs);
        source = this.GetComponent<Light>();
        is_directional = (source.type == LightType.Directional);
        is_spot = (source.type == LightType.Spot);

        if (!is_spot && use_raycasting)
        {
            use_raycasting = false;
            Debug.Log("Warning: Ray tracing is enabled but is only supported for spotlights. Disabled.");
        }

        GetAllVPLs();
        GetAllBlockers();

        if (use_raycasting)
        {
            Vector3 center = source.transform.position; 
            Vector3 normal = source.transform.forward;
            if(numberOfDynamicVPLs<1)
            {
                numberOfDynamicVPLs=1;
            }
                dynamicVPLParent = GameObject.Find("DynamicVPLs");
            if(dynamicVPLParent == null)
            {
                dynamicVPLParent = new GameObject("DynamicVPLs");
            }
            for (int i = 0; i < numberOfDynamicVPLs; i++)
            {
                GameObject dynamicVPL_go = new GameObject();
                dynamicVPL_go.transform.parent = dynamicVPLParent.transform; 
                Light dynamicVPL  = dynamicVPL_go.AddComponent<Light>();
                dynamicVPL.type = LightType.Spot;
                dynamicVPL.innerSpotAngle = 0;
                dynamicVPL.spotAngle = 160;
                dynamicVPL.range = source.range;
                dynamicVPL.cookie = brdf_cookie;
                dynamicVPL_go.name = $"DynamicVPL-{i}";
                dynamicVPLList.Add(dynamicVPL);
            }
        }
    }

    void Update()
    {
        if (numberOfDynamicVPLs < 1)
            numberOfDynamicVPLs = 1;

        if (automatic_weights)
            UpdateWeightsAmortized();

        Transform FL = source.transform;
        Vector3 dir = FL.forward;
        Vector3 pos = FL.position;
        float source_intensity = source.intensity;
        Color source_color = source.color;

        if (use_raycasting)
        {
            RaycastHit hit;
            if (!Physics.Raycast(pos, dir, out hit))
            {
                foreach (var dynamicVPL in dynamicVPLList)
                    dynamicVPL.enabled = false;
                return;
            }

            float dist = hit.distance / distance_scale;
            float intensity = source_intensity / (0.1f + dist * dist);

            if (old_vpl_pos.magnitude == 0.0)
                old_vpl_pos = hit.point;
            Vector3 dynamicVPL_pos = smooth ? 0.5f * (hit.point + old_vpl_pos) : hit.point;
            old_vpl_pos = dynamicVPL_pos;

            radius = Mathf.Clamp(radius, -1f, 1f);

            // Spawn all dynamic VPLs around the raycast point
            for (int i = 0; i < dynamicVPLList.Count; i++)
            {
                Vector3 castDirection = dir;
                Vector3 rayOrigin = pos;
                Vector3 targetPosition;

                if (i == 0)
                {
                    if (Physics.Raycast(pos, dir, out RaycastHit hitLocal))
                    {
                        targetPosition = smooth ? 0.5f * (hitLocal.point + old_vpl_pos) : hitLocal.point;
                        old_vpl_pos = targetPosition;
                    }
                    else
                    {
                        dynamicVPLList[i].enabled = false;
                        continue;
                    }
                }
                else
                {
                    float angle = (360f / (numberOfDynamicVPLs - 1)) * i;
                    // Build tangent space around the hit.normal
                    Vector3 tangent = Vector3.Cross(hit.normal, Vector3.up);
                    if (tangent.sqrMagnitude < 0.001f)
                        tangent = Vector3.Cross(hit.normal, Vector3.right);
                    tangent.Normalize();
                    Vector3 bitangent = Vector3.Cross(hit.normal, tangent);

                    // Generate a circular offset in the tangent plane
                    angle = (360f / (numberOfDynamicVPLs - 1)) * i;
                    float radians = angle * Mathf.Deg2Rad;
                    Vector3 offsetDir = Mathf.Cos(radians) * tangent + Mathf.Sin(radians) * bitangent;

                    // Final cast direction: slightly jittered from the original direction `dir`, constrained to hemisphere
                    castDirection = (dir + offsetDir * radius).normalized;



                    if (Physics.Raycast(rayOrigin, castDirection, out RaycastHit hitLocal))
                    {
                        targetPosition = hitLocal.point;
                    }
                    else
                    {
                        dynamicVPLList[i].enabled = false;
                        continue;
                    }
                }

                dynamicVPLList[i].transform.position = targetPosition;
                dynamicVPLList[i].color = new Color(0, 0, 0); // clear for accumulation
            }

            float w_total = 0.0f;
            Vector3 vpl_normal = Vector3.zero;
            float area_factor = 0.0f;

            for (int i = 0; i < lights.Count; i++)
            {
                Vector3 light_pos = lights[i].transform.position;

                for (int b = 0; b < dynamicVPLList.Count; b++)
                {
                    var dynamicVPL = dynamicVPLList[b];
                    Vector3 to_vpl = light_pos - dynamicVPL.transform.position;
                    float vpl_dist = to_vpl.magnitude / distance_scale;
                    float w = 1.0f / (0.005f + vpl_dist * vpl_dist);

                    dynamicVPL.color += w * lights[i].color;

                    area_factor += weights[i] * w;
                    vpl_normal += lights[i].type == LightType.Spot ? w * lights[i].transform.forward : -w * dir;
                    w_total += w;
                }
            }

            vpl_normal = hit.normal;
            foreach (var dynamicVPL in dynamicVPLList)
            {
                dynamicVPL.color = dynamicVPL.color * source_color / w_total;
                dynamicVPL.transform.forward = smooth ? 0.5f * (old_vpl_normal + vpl_normal.normalized) : vpl_normal.normalized;
                dynamicVPL.enabled = true;
            }
            old_vpl_normal = vpl_normal.normalized;

            float cos_theta_i = Mathf.Max(Vector3.Dot(vpl_normal.normalized, -dir), 0.0f);
            intensity *= cos_theta_i * (area_factor / w_total);
            intensity *= intensityBoost;

            foreach (var dynamicVPL in dynamicVPLList)
                dynamicVPL.intensity = intensity;

            if (secondary_bounce)
            {
                Vector3 clusterCenter = Vector3.zero;
                foreach (var dynamicVPL in dynamicVPLList)
                    clusterCenter += dynamicVPL.transform.position;
                clusterCenter /= dynamicVPLList.Count;

                Vector3[] hemisphereDirections = GenerateHemisphereDirections(hit.normal, numOfSecondaryVPLs);

                for (int i = 0; i < numOfSecondaryVPLs; i++)
                {
                    Vector3 bounceDirection = hemisphereDirections[i];
                    Vector3 origin = hit.point + hit.normal * 0.01f;

                    if (Physics.Raycast(origin, bounceDirection, out RaycastHit secondaryHit, 10f, layerMask))
                    {
                        Vector3 bouncePosition = secondaryHit.point;
                        Vector3 bounceNormal = secondaryHit.normal;

                        //Debug.DrawLine(hit.point, bouncePosition, Color.cyan, 2f);

                        Light secVPL = secondaryVPLs[i];
                        secVPL.transform.position = bouncePosition;
                        secVPL.transform.forward = bounceNormal;
                        Color surfaceColor = Color.white;
                        if (secondaryHit.collider.TryGetComponent<Renderer>(out Renderer rend))
                        {
                            surfaceColor = rend.material.color;
                        }
                        Color firstBounceColor = dynamicVPLList[0].color;
                        secVPL.color = firstBounceColor * surfaceColor;

                        float myDistance = Vector3.Distance(dynamicVPLList[0].transform.position, bouncePosition);
                        Vector3 toOriginalHit = (hit.point - bouncePosition).normalized;
                        float cosineWeight = Mathf.Max(0f, Vector3.Dot(bounceNormal.normalized, toOriginalHit));

                        float secIntensity=boost2ndIntensity*dynamicVPLList[0].intensity / (0.1f + myDistance * myDistance);
                        secIntensity *= cosineWeight;

                        secVPL.intensity = secIntensity;
                        secVPL.enabled = true;
                    }
                    else
                    {
                        secondaryVPLs[i].enabled = false;
                    }
                }
            }

            return;
        }

        // Non-raycasting fallback mode
        float sec_intensity = 0.0f;
        Vector3 sec_pos = Vector3.zero;
        Vector3 sec_dir = Vector3.zero;
        Color sec_color = new Color();
        float sec_weight = 0.0f;

        for (int i = 0; i < lights.Count; i++)
        {
            Vector3 light_pos = lights[i].transform.position;
            Vector3 to_vpl = is_directional ? source.transform.forward : light_pos - pos;
            Vector3 to_vpl_normalized = to_vpl.normalized;
            float dot = Vector3.Dot(to_vpl_normalized, dir);

            float intensity = source_intensity * weights[i];
            if (is_spot)
            {
                float angle_cos = Mathf.Cos(Mathf.PI * this.GetComponent<Light>().spotAngle / 180.0f);
                intensity *= Mathf.Max(0.0f, (dot - angle_cos) / (1.0f - angle_cos));
            }
            if (!is_directional)
            {
                float dist = to_vpl.magnitude / distance_scale;
                intensity *= 1.0f / (0.1f + dist * dist);
            }

            if (is_spot || is_directional)
            {
                Vector3 vpl_normal = lights[i].transform.forward;
                dot = Mathf.Max(0.0f, Vector3.Dot(to_vpl_normalized, -vpl_normal));
                intensity *= dot;
            }

            if (blockers.Count > 0)
            {
                Vector3 endpoint = is_directional ? light_pos - 100.0f * to_vpl_normalized : pos;

                for (int j = 0; j < blockers.Count; j++)
                {
                    float dist_to_blocker = PointToSegmentDistanceSquared(blockers[j].transform.position, endpoint, light_pos);
                    float range = blockers[j].range;
                    float filter = Mathf.Min(1.0f, dist_to_blocker / (0.0001f + range * range));
                    intensity *= filter;
                }
            }

            if (intensity <= 0.01f)
            {
                lights[i].enabled = false;
            }
            else
            {
                lights[i].enabled = true;
                lights[i].intensity = intensity;
                lights[i].color = source_color * reflectance[i];
            }

            if (secondary_bounce)
            {
                float w = intensity / source_intensity;
                sec_intensity += w * avg_refl * intensity;
                sec_color += w * lights[i].color;
                sec_pos += w * light_pos;
                sec_dir -= w * lights[i].transform.forward;
                sec_weight += w + 0.001f;
            }
        }

        if (secondary_bounce)
        {
            dynamic_vpl_secondary.transform.position = sec_pos / sec_weight - dir * avg_secondary_distance;
            dynamic_vpl_secondary.transform.forward = sec_dir.normalized;
            dynamic_vpl_secondary.intensity = sec_intensity / (sec_weight * avg_secondary_distance * avg_secondary_distance);
            dynamic_vpl_secondary.color = sec_color / sec_weight;
        }
    }



    Vector3[] GenerateHemisphereDirections(Vector3 normal, int numSamples, int seed = 713)//713=random number
    {
        Vector3[] directions = new Vector3[numSamples];

        // Create orthonormal basis (Tangent, Bitangent, Normal)
        Vector3 tangent;
        if (Mathf.Abs(normal.y) < 0.999f)
            tangent = Vector3.Cross(normal, Vector3.up).normalized;
        else
            tangent = Vector3.Cross(normal, Vector3.right).normalized;

        Vector3 bitangent = Vector3.Cross(normal, tangent);

        System.Random prng = new System.Random(seed);

        for (int i = 0; i < numSamples; i++)
        {
            // Use PRNG instead of UnityEngine.Random for stable repeatability
            float u1 = (float)prng.NextDouble();
            float u2 = (float)prng.NextDouble();

            // Cosine-weighted hemisphere sampling
            float r = Mathf.Sqrt(u1);
            float theta = 2f * Mathf.PI * u2;

            float x = r * Mathf.Cos(theta);
            float y = r * Mathf.Sin(theta);
            float z = Mathf.Sqrt(1f - u1);

            // Convert to world space
            Vector3 sampleDir = x * tangent + y * bitangent + z * normal;

            directions[i] = sampleDir.normalized;
        }

        return directions;
    }



    void InitializeSecondaryVPLs(int count)
    {
        // Clear old lights if any
        foreach (var light in secondaryVPLs)
            if (light != null)
                Destroy(light.gameObject);
        secondaryVPLs.Clear();

        GameObject secondaryParent = GameObject.Find("DynamicVPLs-Secondary");
        if (secondaryParent == null)
        {
            secondaryParent = new GameObject("DynamicVPLs-Secondary");
            secondaryParent.transform.position = Vector3.zero;
            secondaryParent.transform.rotation = Quaternion.identity;
        }

        for (int i = 0; i < count; i++)
        {
            GameObject vplGO = new GameObject("DynamicVPL-secondary-" + i);
            vplGO.transform.parent = secondaryParent.transform;

            Light lightComp = vplGO.AddComponent<Light>();
            lightComp.type = LightType.Spot;
            lightComp.innerSpotAngle = 0;
            lightComp.spotAngle = 160;
            lightComp.cookie = brdf_cookie;
            lightComp.range = 5f;

            secondaryVPLs.Add(lightComp);
        }
    }


    void UpdateSecondaryDynamicVPL(int index, Vector3 position, Vector3 normal, Color color, float intensity)
    {
        if (index < 0 || index >= secondaryVPLs.Count)
            return;

        Light vplLight = secondaryVPLs[index];
        vplLight.transform.position = position;
        vplLight.transform.forward = normal;
        vplLight.color = color;
        vplLight.intensity = intensity;
    }
}