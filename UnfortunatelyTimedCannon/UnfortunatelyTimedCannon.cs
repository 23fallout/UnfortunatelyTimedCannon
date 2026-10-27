using HarmonyLib;
using OWML.Common;
using OWML.ModHelper;
using System.Reflection;
using System.Collections.Generic;
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
        private float _flightTimer;
        private Vector3 _lastProbePos;
        private static readonly FieldInfo ImpactEventField = AccessTools.Field(typeof(ImpactSensor), "OnImpact");
        private NomaiWarpTransmitter _timeLoopPad;
        private bool _padSearched;
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
        private bool _inVessel;
        public float triggerChance = 0.25f;
        private bool _wasOnPad;
        public float musicRetrySeconds = 30f;
        private readonly Dictionary<global::AudioType, OWAudioSource> _activeTriggerSources = new Dictionary<global::AudioType, OWAudioSource>();
        private readonly Dictionary<global::AudioType, float> _cooldownUntil = new Dictionary<global::AudioType, float>();

        private float _nextOrbitLog;

        public override void Configure(IModConfig config)
        {
            randomFireChance = config.GetSettingsValue<float>("Random Fire Chance");
            randomFireInterval = config.GetSettingsValue<float>("Random Fire Interval");
        }
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
            PinProbe();

            if (_inSolarSystem)
            {
                var astro = Locator.GetAstroObject(AstroObject.Name.ProbeCannon);
                //if (astro != null) LogOrbit(astro.GetOWRigidbody());
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

            CheckMusicRetries();

            if (!_hasFired) CheckPad();
            if (!_hasFired) CheckRandomFire();
            if (!_hasFired) CheckVessel();

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

                    float steerSpeed = _flightTimer < 3f ? 5f : 2f;

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

        private readonly HashSet<global::AudioType> _triggerTracks = new HashSet<global::AudioType>
        {
            global::AudioType.BH_Observatory,
            global::AudioType.SadNomaiTheme,
            global::AudioType.DB_VesselDiscovery,
        };


        public void OnTrackPlayed(OWAudioSource source)
        {
            if (!_inSolarSystem || source == null) return;
            if (_hasFired || _probeSpent) return;

            var track = source.audioLibraryClip;
            if (!_triggerTracks.Contains(track)) return;

            _activeTriggerSources[track] = source;

            if (_cooldownUntil.TryGetValue(track, out float until) && Time.time < until) return;

            RollForTrack(track);
        }

        private void RollForTrack(global::AudioType track)
        {
            if (TryFire())
            {
                _activeTriggerSources.Clear();
                _cooldownUntil.Clear();
            }
            else
            {
                _cooldownUntil[track] = Time.time + musicRetrySeconds;
                ModHelper.Console.WriteLine($"{track}: cooling down for {musicRetrySeconds:F0}s", MessageType.Info);
            }
        }

        private void CheckMusicRetries()
        {
            if (_activeTriggerSources.Count == 0) return;

            if (_hasFired || _probeSpent)
            {
                _activeTriggerSources.Clear();
                _cooldownUntil.Clear();
                return;
            }

            foreach (var track in new List<global::AudioType>(_activeTriggerSources.Keys))
            {
                if (!_activeTriggerSources.TryGetValue(track, out var src)) continue;

                if (src == null || !src.isPlaying)      
                {
                    _activeTriggerSources.Remove(track);
                    continue;
                }

                if (!_cooldownUntil.TryGetValue(track, out float until) || Time.time >= until)
                    RollForTrack(track);
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

        private void CheckVessel()
        {
            var det = Locator.GetPlayerSectorDetector();
            if (det == null) return;

            bool inside = det.IsWithinSector(Sector.Name.Vessel);
            if (inside != _inVessel)
            {
                _inVessel = inside;
                ModHelper.Console.WriteLine(inside ? "Inside Vessel" : "Outside Vessel", MessageType.Info);
                if (inside) TryFire();            
            }
        }

        public bool TryFire()
        {
            if (_hasFired || _probeSpent) return false;

            float roll = Random.value;
            bool hit = roll < triggerChance;
            ModHelper.Console.WriteLine(
                $"Trigger roll {roll:F2} (need < {triggerChance:F2}): {(hit ? "FIRE" : "no")}",
                MessageType.Info);

            if (hit) CannonFire();
            return hit;
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

            bool onPad = _timeLoopPad.IsPlayerOnPlatform();
            if (onPad && !_wasOnPad) TryFire();     
            _wasOnPad = onPad;
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
            Set("contactSurfaceTypes", new SurfaceType[] { SurfaceType.None });
            Set("thisBody", victim);
            Set("otherBody", _probeBody);
            Set("otherCollider", probeCol);
            Set("point", victim.transform.position);
            Set("normal", -relativeVelocity.normalized);
            Set("thisCollider", victim.GetComponentInChildren<Collider>());

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
            _inVessel = false;
            _wasOnPad = false;
            _activeTriggerSources.Clear();
            _cooldownUntil.Clear();

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
                    UnfortunatelyTimedCannon.Instance.TryFire();
            }
        }

        [HarmonyPatch(typeof(SunStationMusicController), "OnOpenEmergencyHatch")]
        public static class SunStationHatchPatch
        {
            static void Postfix()
            {
                UnfortunatelyTimedCannon.Instance.TryFire();
            }
        }

        [HarmonyPatch]
        public static class MusicPlayPatch
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                string[] names = { "Play", "PlayDelayed", "PlayWithLibraryVolume", "FadeIn", "FadeInToLibraryVolume" };
                foreach (var m in AccessTools.GetDeclaredMethods(typeof(OWAudioSource)))
                    if (System.Array.IndexOf(names, m.Name) >= 0)
                        yield return m;
            }

            static void Postfix(OWAudioSource __instance)
            {
                UnfortunatelyTimedCannon.Instance.OnTrackPlayed(__instance);
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