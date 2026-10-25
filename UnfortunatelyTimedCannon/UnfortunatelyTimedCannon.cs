using HarmonyLib;
using OWML.Common;
using OWML.ModHelper;
using System.Reflection;
using UnityEngine;

namespace UnfortunatelyTimedCannon
{
    public class UnfortunatelyTimedCannon : ModBehaviour
    {
        public static UnfortunatelyTimedCannon Instance;
        public static bool AllowFire;

        public float rotateMaxCatchUpSpeed = 90f;
        public float catchUpGain = 3f;
        private Transform _target;
        private bool _inSolarSystem;
        private bool _hasFired;
        private bool _probeSpent;
        private Vector3 _lastDir;
        private bool _hasLastDir;
        private OrbitalProbeLaunchController _ctrl;
        private OWRigidbody _probeBody;
        public float homingStrength = 1500f;
        private Vector3 _initialCannonUp;
        private float _flightTimer;
        private Vector3 _lastProbePos;
        private static readonly FieldInfo ImpactEventField = AccessTools.Field(typeof(ImpactSensor), "OnImpact");
        private NomaiWarpTransmitter _timeLoopPad;
        private bool _padSearched;
        private SunStationMusicController _OnOpenEmergancyHatch;
        public float randomFireChance = 0.005f;
        public float randomFireInterval = 60f;
        private float _nextRandomRoll;
        public bool testSkipTracking = false; 
        public bool testSkipRotation = false;   
        public bool testSkipProbeSetup = false;   
        private bool _probeDocked;
        private Vector3 _probeLocalPos;
        private Quaternion _probeLocalRot;
        private Vector3 _origAngVel;
        private bool _angVelSaved;

        private float _nextOrbitLog;


        public void Awake()
        {
            Instance = this;
        }

        public void Start()
        {
            ModHelper.Console.WriteLine($"My mod {nameof(UnfortunatelyTimedCannon)} is loaded!", MessageType.Success);

            new Harmony("23f.Cannontest").PatchAll(Assembly.GetExecutingAssembly());

            LoadManager.OnCompleteSceneLoad += OnCompleteSceneLoad;
            //   GlobalMessenger.AddListener("SuitUp", CannonFire);
        }

        private void OnDestroy()
        {
            LoadManager.OnCompleteSceneLoad -= OnCompleteSceneLoad;
            //  GlobalMessenger.RemoveListener("SuitUp", CannonFire);
        }


        private void FixedUpdate()
        {
            if (!testSkipTracking) DoTracking();

            if (!testSkipTracking) DoTracking();
            PinProbe();

            if (_inSolarSystem)
            {
                var astro = Locator.GetAstroObject(AstroObject.Name.ProbeCannon);
                if (astro != null) LogOrbit(astro.GetOWRigidbody());
            }
        }

        private void LogOrbit(OWRigidbody cannon)
        {
            if (cannon == null || Time.time < _nextOrbitLog) return;
            _nextOrbitLog = Time.time + 2f;

            var gd = Locator.GetAstroObject(AstroObject.Name.GiantsDeep);
            if (gd == null) return;
            var vol = gd.GetGravityVolume();
            var gdBody = gd.GetOWRigidbody();
            if (vol == null || gdBody == null) return;

            float r = Vector3.Distance(cannon.GetWorldCenterOfMass(), gdBody.GetWorldCenterOfMass());
            float speed = (cannon.GetVelocity() - gdBody.GetVelocity()).magnitude;
            float K = vol.CalculateForceAccelerationOnBody(cannon).magnitude * r;  
            float energy = speed * speed * 0.5f + K * Mathf.Log(r);              

        }

        private void DoTracking()
        {
            if (!_inSolarSystem) return;
            if (_ctrl == null) return;

            if (!_hasFired) CheckPad();
            if (!_hasFired) CheckRandomFire();

            if (_hasFired && _probeBody != null)
            {
                ReleaseCannonRotation();
                _flightTimer += Time.fixedDeltaTime;
                Vector3 currentProbePos = _probeBody.transform.position;

                Transform targetTransform = Locator.GetPlayerTransform();
                OWRigidbody targetBody = Locator.GetPlayerBody();
                float hitRadius = 8.0f;

                if (PlayerState.IsInsideShip())
                {
                    var shipTransform = Locator.GetShipTransform();
                    if (shipTransform != null) targetTransform = shipTransform;
                    var shipBody = Locator.GetShipBody();
                    if (shipBody != null) targetBody = shipBody;
                    hitRadius = 15.0f;
                }

                if (targetTransform != null)
                {
                    Vector3 targetPos = targetTransform.position;
                    Vector3 closestPoint = GetClosestPointOnSegment(_lastProbePos, currentProbePos, targetPos);
                    float distanceToPath = Vector3.Distance(closestPoint, targetPos);

                    if (distanceToPath < hitRadius)
                    {
                        if (targetBody != null)
                        {
                            Vector3 probeVel = _probeBody.GetVelocity();
                            Vector3 relVel = probeVel - targetBody.GetVelocity();

                            targetBody.AddVelocityChange(probeVel.normalized * 50000f);
                            targetBody.AddAngularVelocityChange(Random.onUnitSphere * 30f);

                            SendImpact(Locator.GetPlayerBody(), relVel);
                            if (PlayerState.IsInsideShip())
                                SendImpact(Locator.GetShipBody(), relVel);
                        }
                        _probeSpent = true;
                        _hasFired = false;
                        return;
                    }

                    Vector3 toTarget = targetPos - currentProbePos;
                    float speed = _probeBody.GetVelocity().magnitude;

                    float steerSpeed = _flightTimer < 3f ? 5f : 0.4f;

                    Vector3 steeredVel = Vector3.RotateTowards(_probeBody.GetVelocity(), toTarget.normalized * speed, steerSpeed * Time.fixedDeltaTime, 0f);
                    _probeBody.SetVelocity(steeredVel);
                }

                _lastProbePos = currentProbePos;
                return;
            }

            if (_target == null) CannonTrack();
            if (_target == null) return;

            Vector3 playerPos = _target.position;
            Vector3 playerVel = Vector3.zero;
            var playerBody = Locator.GetPlayerBody();
            if (playerBody != null)
            {
                Vector3 cannonVel = _ctrl.GetComponent<OWRigidbody>() != null ? _ctrl.GetComponent<OWRigidbody>().GetVelocity() : Vector3.zero;
                playerVel = playerBody.GetVelocity() - cannonVel;
            }

            float distance = Vector3.Distance(_ctrl.transform.position, playerPos);
            float estimatedTime = distance / 2500f;

            Vector3 predictedPos = playerPos + (playerVel * estimatedTime);

            Vector3 dir = predictedPos - _ctrl.transform.position;
            if (dir.sqrMagnitude < 0.01f) return;

            Quaternion current = _ctrl.transform.rotation;
            Vector3 forward = dir.normalized;
            Vector3 upward = _ctrl.transform.up;
            Vector3.OrthoNormalize(ref forward, ref upward);

            Quaternion desired = Quaternion.LookRotation(forward, upward);

            float trackDegPerSec = _hasLastDir ? Vector3.Angle(_lastDir, dir) / Time.fixedDeltaTime : 0f;
            _lastDir = dir;
            _hasLastDir = true;

            float error = Quaternion.Angle(current, desired);
            float catchUp = Mathf.Min(rotateMaxCatchUpSpeed, error * catchUpGain);

            Quaternion next = error < 0.05f
                ? desired
                : Quaternion.RotateTowards(current, desired, (trackDegPerSec + catchUp) * Time.fixedDeltaTime);

            if (!testSkipRotation)
            {
                var cannonBody = _ctrl.GetComponent<OWRigidbody>();
                if (cannonBody != null)
                {
                    if (!_angVelSaved)
                    {
                        _origAngVel = cannonBody.GetAngularVelocity();
                        _angVelSaved = true;
                    }

                    Quaternion delta = next * Quaternion.Inverse(current);
                    delta.ToAngleAxis(out float angleDeg, out Vector3 axis);
                    if (angleDeg > 180f) angleDeg -= 360f;

                    Vector3 omega = Vector3.zero;
                    if (Mathf.Abs(angleDeg) > 0.001f && !float.IsInfinity(axis.x) && !float.IsNaN(axis.x))
                        omega = axis * (angleDeg * Mathf.Deg2Rad / Time.fixedDeltaTime);

                    cannonBody.SetAngularVelocity(omega);
                }
                else
                {
                    _ctrl.transform.rotation = next;
                }
            }
        }


        private Vector3 GetClosestPointOnSegment(Vector3 s1, Vector3 s2, Vector3 p)
        {
            Vector3 s = s2 - s1;
            float len = s.sqrMagnitude;
            if (len < 0.0001f) return s1;
            float t = Vector3.Dot(p - s1, s) / len;
            t = Mathf.Clamp01(t);
            return s1 + t * s;
        }

        private void LateUpdate()
        {
            PinProbe();
        }

        private void PinProbe()
        {
            if (!_probeDocked || _probeBody == null || _ctrl == null) return;

            _probeBody.transform.SetPositionAndRotation(
                _ctrl.transform.TransformPoint(_probeLocalPos),
                _ctrl.transform.rotation * _probeLocalRot);
        }

        private void ReleaseCannonRotation()
        {
            if (!_angVelSaved || _ctrl == null) return;
            var body = _ctrl.GetComponent<OWRigidbody>();
            if (body != null) body.SetAngularVelocity(_origAngVel);  
        }

        public void CannonTrack()
        {
            _target = Locator.GetPlayerTransform();
            _hasLastDir = false;
        }

        public void CannonFire()
        {
            if (_hasFired || _probeSpent) return;

            if (_ctrl == null)
            {
                _ctrl = FindObjectOfType<OrbitalProbeLaunchController>();
                if (_ctrl == null) return;
            }

            AllowFire = true;

            try
            {
                AccessTools.Method(
                    typeof(OrbitalProbeLaunchController),
                    "LaunchProbe"
                ).Invoke(_ctrl, null);

                _hasFired = true;
                if (_probeBody != null)
                {
                    _lastProbePos = _probeBody.transform.position;
                }
            }
            finally
            {
                AllowFire = false;
            }
        }

        private void CheckPad()
        {
            if (!_padSearched)
            {
                _padSearched = true;
                foreach (var t in FindObjectsOfType<NomaiWarpTransmitter>())
                {
                    if (t.GetFrequency() == NomaiWarpPlatform.Frequency.TimeLoop)
                    {
                        _timeLoopPad = t;
                        break;
                    }
                }
            }

            if (_timeLoopPad == null) return;

            if (_timeLoopPad.IsPlayerOnPlatform())
                CannonFire();
        }

        private void SendImpact(OWRigidbody victim, Vector3 relativeVelocity)
        {
            if (victim == null) return;
            var sensor = victim.GetComponentInChildren<ImpactSensor>();
            if (sensor == null) return;

            var data = (ImpactData)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(ImpactData));
            var probeCol = _probeBody != null ? _probeBody.GetComponentInChildren<Collider>() : null;

            void Set(string field, object value) => AccessTools.Field(typeof(ImpactData), field).SetValue(data, value);
            Set("velocity", relativeVelocity);
            Set("speed", relativeVelocity.magnitude);
            Set("contactSurfaceTypes", new SurfaceType[0]);
            Set("thisBody", victim);
            Set("otherBody", _probeBody);
            Set("otherCollider", probeCol);
            Set("point", victim.transform.position);
            Set("normal", -relativeVelocity.normalized);

            var del = ImpactEventField.GetValue(sensor) as System.Delegate;
            if (del == null) return;

            foreach (var d in del.GetInvocationList())
            {
                try { d.DynamicInvoke(data); }
                catch (System.Exception e)
                {
                    ModHelper.Console.WriteLine($"Impact handler error: {e.InnerException?.Message ?? e.Message}", MessageType.Warning);
                }
            }
        }

        private void CheckRandomFire()
        {
            if (Time.time < _nextRandomRoll) return;
            _nextRandomRoll = Time.time + randomFireInterval;

            if (Random.value < randomFireChance)
                CannonFire();
        }

        public void OnCompleteSceneLoad(OWScene previousScene, OWScene newScene)
        {
            _inSolarSystem = newScene == OWScene.SolarSystem;
            _target = null;
            _ctrl = null;
            _probeBody = null;
            _hasLastDir = false;
            _hasFired = false;
            _flightTimer = 0f;
            _timeLoopPad = null;
            _padSearched = false;
            _nextRandomRoll = Time.time + randomFireInterval;
            _probeSpent = false;
            _probeDocked = false;
            _angVelSaved = false;

            if (_inSolarSystem)
            {
                ModHelper.Console.WriteLine("Loaded", MessageType.Success);

                _ctrl = FindObjectOfType<OrbitalProbeLaunchController>();
                if (_ctrl != null)
                {

                    var probeField = AccessTools.Field(typeof(OrbitalProbeLaunchController), "_probeBody");
                    _probeBody = (OWRigidbody)probeField.GetValue(_ctrl);

                    if (_probeBody != null && !testSkipProbeSetup)
                    {
                        _probeBody.transform.SetParent(_ctrl.transform, true);

                        _probeLocalPos = _ctrl.transform.InverseTransformPoint(_probeBody.transform.position);
                        _probeLocalRot = Quaternion.Inverse(_ctrl.transform.rotation) * _probeBody.transform.rotation;

                        _probeBody.MakeKinematic();

                        foreach (var c in _probeBody.GetComponentsInChildren<Collider>(true))
                            c.enabled = false;

                        _probeDocked = true;
                    }
                }
            }
        }

        [HarmonyPatch(typeof(OrbitalProbeLaunchController), "OnStartOfTimeLoop")]
        public static class PreventCannonBreakPatch
        {
            static bool Prefix(OrbitalProbeLaunchController __instance)
            {
                UnfortunatelyTimedCannon.Instance._hasFired = false;
                UnfortunatelyTimedCannon.Instance._target = null;
                UnfortunatelyTimedCannon.Instance._hasLastDir = false;
                UnfortunatelyTimedCannon.Instance._probeSpent = false;
                UnfortunatelyTimedCannon.Instance._flightTimer = 0f;

                var showBreakField = AccessTools.Field(typeof(OrbitalProbeLaunchController), "_showCannonBreaking");
                showBreakField.SetValue(__instance, false);

                var proxiesField = AccessTools.Field(typeof(OrbitalProbeLaunchController), "_realDebrisSectorProxies");
                var proxies = (SectorProxy[])proxiesField.GetValue(__instance);
                if (proxies != null)
                {
                    foreach (var proxy in proxies)
                    {
                        if (proxy != null && proxy.gameObject != null)
                        {
                            proxy.gameObject.SetActive(true);
                        }
                    }
                }

                var fakeDebrisField = AccessTools.Field(typeof(OrbitalProbeLaunchController), "_fakeDebrisBodies");
                var fakeDebrisBodies = (OWRigidbody[])fakeDebrisField.GetValue(__instance);
                if (fakeDebrisBodies != null)
                {
                    foreach (var debris in fakeDebrisBodies)
                    {
                        if (debris != null && debris.gameObject != null)
                        {
                            Object.Destroy(debris.gameObject);
                        }
                    }
                }

                return false;
            }
        }

        [HarmonyPatch(typeof(OrbitalProbeLaunchController), "FixedUpdate")]
        public static class OrbitalProbeFixedUpdatePatch
        {
            static bool Prefix()
            {
                return false;
            }
        }

        [HarmonyPatch(typeof(SunStationMusicController), "OnEnterControlModule")]
        public static class SunStationControlModulePatch
        {
            static void Postfix(GameObject hitObj)
            {
                if (hitObj.CompareTag("PlayerDetector"))
                    UnfortunatelyTimedCannon.Instance.CannonFire();
            }
        }

        [HarmonyPatch(typeof(SunStationMusicController), "OnOpenEmergencyHatch")]
        public static class SunStationHatchPatch
        {
            static void Postfix()
            {
                UnfortunatelyTimedCannon.Instance.CannonFire();
            }
        }

        [HarmonyPatch(typeof(OrbitalProbeLaunchController), "LaunchProbe")]
        public static class CustomLaunchProbePatch
        {
            static bool Prefix(OrbitalProbeLaunchController __instance)
            {


                var probeBodyField = AccessTools.Field(typeof(OrbitalProbeLaunchController), "_probeBody");
                var probeBody = (OWRigidbody)probeBodyField.GetValue(__instance);

                if (probeBody == null) return false;

                UnfortunatelyTimedCannon.Instance._probeDocked = false;  

                probeBody.transform.SetParent(null, true);
                foreach (var c in probeBody.GetComponentsInChildren<Collider>(true))
                    c.enabled = true;

                probeBody.MakeNonKinematic();
                probeBody.EnableCollisionDetection();
                var rb = probeBody.GetRigidbody();
                if (rb != null)
                    rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                Transform targetTransform = Locator.GetPlayerTransform();
                OWRigidbody targetBody = Locator.GetPlayerBody();

                if (PlayerState.IsInsideShip())
                {
                    var shipTransform = Locator.GetShipTransform();
                    if (shipTransform != null) targetTransform = shipTransform;

                    var shipBody = Locator.GetShipBody();
                    if (shipBody != null) targetBody = shipBody;
                }

                Vector3 targetPos = targetTransform != null ? targetTransform.position : __instance.transform.position + __instance.transform.forward;

                Vector3 targetVel = Vector3.zero;
                if (targetBody != null)
                {
                    Vector3 cannonVel = __instance.GetComponent<OWRigidbody>() != null ? __instance.GetComponent<OWRigidbody>().GetVelocity() : Vector3.zero;
                    targetVel = targetBody.GetVelocity() - cannonVel;
                }

                float dist = Vector3.Distance(__instance.transform.position, targetPos);

                var cannonRb = __instance.GetComponent<OWRigidbody>();
                if (cannonRb != null) probeBody.SetVelocity(cannonRb.GetVelocity());
                Vector3 predictedTarget = targetPos + (targetVel * (dist / 2500f));

                Vector3 fireDirection = (predictedTarget - __instance.transform.position).normalized;

                probeBody.AddVelocityChange(fireDirection * 2500f);
                probeBody.AddAngularVelocityChange(probeBody.transform.right * 0.1f);

                var particlesField = AccessTools.Field(typeof(OrbitalProbeLaunchController), "_launchParticles");
                var particles = (ParticleSystem[])particlesField.GetValue(__instance);
                if (particles != null)
                {
                    foreach (var ps in particles)
                    {
                        if (ps != null) ps.Play();
                    }
                }
                return false;
            }
        }
    }
}