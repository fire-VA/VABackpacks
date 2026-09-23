// HoboStickPhysicsSetup — runtime configuration of the swinging-bag
// pendulum on the HoboStick prefab.
//
// The HoboStick prefab is authored with two AttachPoint markers:
//   • Stick > AttachPoint  — top of the stick (anchor)
//   • Potli > AttachPoint  — top of the bag (swing pivot)
//
// Each carries Rigidbody + collider components, plus the bag's
// AttachPoint has a ConfigurableJoint. We can't get the bag mesh
// (Potli) to swing visually with this layout — physics would drive
// the AttachPoint child but the bag mesh (its parent) is positioned
// by the prefab's transform hierarchy and ignores the child.
//
// Fix: at prefab-load time we reparent so AttachPoint becomes the
// PARENT of Potli (originally Potli was the parent of AttachPoint).
// Now physics drives AttachPoint, and Potli follows as its child.
// Then we configure the components for a damped pendulum:
//   • Stick anchor → kinematic Rigidbody (transform-driven, follows the stick)
//   • Bag → dynamic Rigidbody with gravity, joint-tethered to stick anchor
//   • Joint → all linear motion locked, all rotations limited (lantern swing)
//
// Called once per prefab load from VAItemManager.LoadSingleItem.

using UnityEngine;

namespace VerdantsAscent.Utilities
{
    public static class HoboStickPhysicsSetup
    {
        public static void Configure(GameObject hoboStickPrefab)
        {
            if (hoboStickPrefab == null) return;

            var attachRoot = hoboStickPrefab.transform.Find("attach");
            if (attachRoot == null)
            {
                Debug.LogWarning("[HoboStickPhysicsSetup] 'attach' child missing — skipping pendulum setup.");
                return;
            }

            // Walk every model variant under attach/. The prefab has multiple
            // (equiped, model, model (1), …) for different display states;
            // configure each one that has a Stick+Potli pair so swing works
            // whichever variant Valheim activates at runtime.
            int configured = 0;
            for (int i = 0; i < attachRoot.childCount; i++)
            {
                var modelRoot = attachRoot.GetChild(i);
                if (TryConfigureVariant(modelRoot))
                    configured++;
            }

            Debug.Log($"[HoboStickPhysicsSetup] Configured {configured} pendulum variant(s) on {hoboStickPrefab.name}.");
        }

        private static bool TryConfigureVariant(Transform modelRoot)
        {
            // Find Stick + Potli children. Some variants are simpler models
            // without these (e.g. an icon-only mesh), and we just skip those.
            var stick = FindChildByName(modelRoot, "Stick");
            var potli = FindChildByName(modelRoot, "Potli");
            if (stick == null || potli == null) return false;

            var stickAttach = stick.Find("AttachPoint");
            var potliAttach = potli.Find("AttachPoint");
            if (stickAttach == null || potliAttach == null) return false;

            ConfigureStickAnchor(stickAttach);
            ConfigureBagPendulum(potli, potliAttach, stickAttach);
            return true;
        }

        private static void ConfigureStickAnchor(Transform stickAttach)
        {
            // Kinematic Rigidbody — its Transform is driven by the parent
            // hierarchy (the stick), so it follows the stick's motion as
            // the player swings the weapon. Joint uses this as connectedBody.
            var rb = stickAttach.GetComponent<Rigidbody>()
                  ?? stickAttach.gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.interpolation = RigidbodyInterpolation.Interpolate;

            // The sphere collider here is just a positional marker — disable
            // it so the bag's capsule never bounces off the stick anchor.
            // (Joint angular limits handle range-of-motion; we don't need
            // collider-vs-collider forces between the two halves of the rig.)
            var coll = stickAttach.GetComponent<SphereCollider>();
            if (coll != null) coll.enabled = false;
        }

        private static void ConfigureBagPendulum(Transform potliMesh,
                                                 Transform potliAttach,
                                                 Transform stickAttach)
        {
            // === REPARENT ===
            // Authored layout: model (1) > Potli > AttachPoint
            // Target layout:   model (1) > AttachPoint > Potli
            //
            // Reason: a non-kinematic Rigidbody on a child has its world
            // Transform driven by physics. The PARENT (Potli mesh) ignores
            // that — it sits wherever the grandparent puts it. So the
            // AttachPoint swings invisibly while the bag mesh stays still.
            // After this swap, Potli is the AttachPoint's child and follows
            // its transform like normal.
            var originalParent = potliMesh.parent;
            potliAttach.SetParent(originalParent, worldPositionStays: true);
            potliMesh.SetParent(potliAttach, worldPositionStays: true);

            // === BAG PHYSICS ===
            // Dynamic Rigidbody — gravity pulls down, the joint keeps it
            // tethered. Damping is moderate so the swing settles within a
            // second or two; mass is small so weapon swings impart real
            // motion to the bag without it feeling like a lead weight.
            var rb = potliAttach.GetComponent<Rigidbody>()
                  ?? potliAttach.gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = false;
            rb.useGravity = true;
            rb.mass = 0.4f;
            rb.drag = 1.5f;          // Linear damping — settles vertical bobbing.
            rb.angularDrag = 1.5f;   // Angular damping — settles swing oscillation.
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            // The capsule collider stays for visual consistency (you'll see
            // the bag bump if it ever hits geometry), but we set it as a
            // trigger so it can't push the player or other physics bodies
            // around. A swinging bag should not knock crates over.
            var bagColl = potliAttach.GetComponent<CapsuleCollider>();
            if (bagColl != null) bagColl.isTrigger = true;

            // === JOINT (PENDULUM) ===
            var joint = potliAttach.GetComponent<ConfigurableJoint>()
                     ?? potliAttach.gameObject.AddComponent<ConfigurableJoint>();
            var stickRb = stickAttach.GetComponent<Rigidbody>();

            joint.connectedBody = stickRb;
            joint.autoConfigureConnectedAnchor = false;
            // Anchors at each body's local origin: the bag pivots at its
            // own AttachPoint position, connected to the stick at the stick's
            // AttachPoint position. The Editor-time positions of those two
            // markers determine the visual rest pose.
            joint.anchor = Vector3.zero;
            joint.connectedAnchor = Vector3.zero;

            // Lock all linear motion — the bag swings, it doesn't fly off.
            joint.xMotion = ConfigurableJointMotion.Locked;
            joint.yMotion = ConfigurableJointMotion.Locked;
            joint.zMotion = ConfigurableJointMotion.Locked;

            // Limited rotation on every axis. ±60° gives a generous swing
            // without the bag flipping over the top of the stick.
            joint.angularXMotion = ConfigurableJointMotion.Limited;
            joint.angularYMotion = ConfigurableJointMotion.Limited;
            joint.angularZMotion = ConfigurableJointMotion.Limited;
            joint.lowAngularXLimit  = new SoftJointLimit { limit = -60f };
            joint.highAngularXLimit = new SoftJointLimit { limit =  60f };
            joint.angularYLimit     = new SoftJointLimit { limit =  60f };
            joint.angularZLimit     = new SoftJointLimit { limit =  60f };

            // Soft angular drive: a small spring biases the bag toward its
            // rest pose (target rotation = identity = hanging straight down
            // relative to the stick), with damping so it doesn't oscillate
            // forever after a swing.
            var swingDrive = new JointDrive
            {
                positionSpring = 2f,
                positionDamper = 0.5f,
                maximumForce   = float.MaxValue,
            };
            joint.angularXDrive  = swingDrive;
            joint.angularYZDrive = swingDrive;
            joint.targetAngularVelocity = Vector3.zero;
            joint.targetRotation        = Quaternion.identity;
        }

        private static Transform FindChildByName(Transform parent, string name)
        {
            // Direct child lookup first (fast path), then recursive descent
            // for variants that nest the named bone deeper.
            var direct = parent.Find(name);
            if (direct != null) return direct;

            for (int i = 0; i < parent.childCount; i++)
            {
                var found = FindChildByName(parent.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
