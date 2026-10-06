using System;
using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Y4NGZUpgrades.Effects;
using Y4NGZUpgrades.Upgrades;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Y4NGZUpgrades.Patches
{
    internal static partial class CourierDronePatch
    {
        private static void EnsureDroneVisualAt(Vector3 groundPosition)
        {
            if (_droneVisual != null)
            {
                _droneVisual.SetActive(true);
                _droneVisual.transform.position = ToFlightPosition(groundPosition);
                _droneGroundPosition = groundPosition;
                CourierDroneRuntimeAssets.EnsureDroneAudio(_droneVisual);
                UpdateCarriedItemTransforms();
                return;
            }

            _droneVisual = CourierDroneRuntimeAssets.InstantiateDroneVisual(ToFlightPosition(groundPosition), Quaternion.identity)
                ?? CreateFallbackDroneVisual(ToFlightPosition(groundPosition));
            ClearDroneRendererCache();
            _droneGroundPosition = groundPosition;
            CourierDroneRuntimeAssets.EnsureDroneAudio(_droneVisual);
            UpdateCarriedItemTransforms();
        }

        private static GameObject CreateFallbackDroneVisual(Vector3 position)
        {
            GameObject drone = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            drone.name = "Y4NGZ_CourierDrone_Placeholder";
            drone.transform.position = position;
            drone.transform.localScale = Vector3.one * 0.45f;
            Collider collider = drone.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);

            Renderer renderer = drone.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Standard"));
                renderer.material.color = new Color(0.45f, 0.95f, 1f, 0.65f);
            }

            Light light = drone.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(0.45f, 0.95f, 1f);
            light.range = 5f;
            light.intensity = 2f;
            light.shadows = LightShadows.None;
            return drone;
        }

        private static void FaceTravelDirection(Transform transform, float deltaX, float deltaY, float deltaZ)
        {
            Vector3 delta = new Vector3(deltaX, deltaY, deltaZ);
            if (transform == null || delta.sqrMagnitude <= 0.0001f)
                return;

            Vector3 flatDirection = new Vector3(delta.x, 0f, delta.z);
            if (flatDirection.sqrMagnitude <= 0.0001f)
                flatDirection = transform.forward;

            flatDirection.Normalize();
            Vector3 currentFlatForward = new Vector3(transform.forward.x, 0f, transform.forward.z);
            if (currentFlatForward.sqrMagnitude <= 0.0001f)
                currentFlatForward = flatDirection;
            else
                currentFlatForward.Normalize();

            float signedTurn = Vector3.SignedAngle(currentFlatForward, flatDirection, Vector3.up);
            float bank = Mathf.Clamp(-signedTurn * 0.45f, -VISUAL_MAX_BANK, VISUAL_MAX_BANK);
            float pitch = Mathf.Lerp(0f, VISUAL_FORWARD_TILT, Mathf.Clamp01(flatDirection.magnitude));
            Quaternion desired = Quaternion.LookRotation(flatDirection, Vector3.up) * Quaternion.Euler(pitch, 0f, bank);
            transform.rotation = Quaternion.Slerp(transform.rotation, desired, Mathf.Clamp01(Time.deltaTime * VISUAL_TURN_SPEED));
        }
    }
}
