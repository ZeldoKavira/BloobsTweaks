using UnityEngine;

namespace BloobsTweaks
{
    // The game drives its own speed-ups (time-bank 2x/3x, turbo 10x) from OfflineProgression, which always
    // keeps speedMultiplier in step with what it writes to Time.timeScale and sets fixedDeltaTime to
    // 0.02 * speed. We read that as the game's chosen speed and apply ours on top in the same way, so the
    // two stack and the game can still change or reset its own part at any time. Banked time is consumed
    // on a realtime clock, so our multiplier doesn't drain it faster.
    //
    // Unity also caps how much game time one frame may advance (Time.maximumDeltaTime, 1/3 s by default),
    // which quietly limits the real speed to about fps / 3 - 20x at 60 fps - whatever timeScale says. The
    // ECS worlds clamp again on their own (World.MaximumDeltaTime, also 1/3 s), and the Bloob's movement
    // (A* FollowerEntity) runs there, so walking stayed at fps / 3 until both were raised. Above that speed
    // we raise both caps so the requested speed is actually reached, and restore them when we let go.
    internal class GameSpeed : MonoBehaviour
    {
        private const float BaseFixedDeltaTime = 0.02f;
        // Enough headroom for the requested speed down to this frame rate.
        private const float MinFpsAtFullSpeed = 20f;
        // Beyond this a single frame skips too much game time for movement and timers to behave. At 10 s, 200x
        // is still reached at 20 fps.
        private const float MaxFrameStep = 10f;

        private OfflineProgression _offline;
        private float _nextSearch;
        private bool _applied;
        private float _defaultMaxDelta = -1f;
        private float _measured = 1f;

        internal static float GameMultiplier { get; private set; } = 1f;
        internal static float Requested => Time.timeScale;
        // Game seconds per real second, averaged; lower than Requested when frames can't keep up.
        internal static float Actual { get; private set; } = 1f;
        internal static float Fps { get; private set; }

        private void LateUpdate()
        {
            if (_defaultMaxDelta < 0f)
                _defaultMaxDelta = Time.maximumDeltaTime;

            if (_offline == null && Time.unscaledTime >= _nextSearch)
            {
                _offline = Scene.Find<OfflineProgression>();
                _nextSearch = Time.unscaledTime + 1f;
            }

            Measure();

            var gameSpeed = _offline != null && _offline.speedMultiplier > 0f ? _offline.speedMultiplier : 1f;
            GameMultiplier = gameSpeed;

            // Something has paused the game; leave that alone.
            if (Time.timeScale == 0f)
                return;

            var ours = Mathf.Max(0.1f, Plugin.SpeedMultiplier.Value);
            if (Mathf.Approximately(ours, 1f))
            {
                // Only hand control back once; otherwise stay out of the game's way entirely.
                if (_applied)
                {
                    Apply(gameSpeed);
                    SetMaxDelta(_defaultMaxDelta);
                    _applied = false;
                }
                return;
            }

            var target = gameSpeed * ours;
            if (!Mathf.Approximately(Time.timeScale, target))
                Apply(target);
            SetMaxDelta(Mathf.Clamp(target / MinFpsAtFullSpeed, _defaultMaxDelta, MaxFrameStep));
            _applied = true;
        }

        // Checked every frame because ECS worlds can be created after we start (e.g. on loading a save).
        private static void SetMaxDelta(float maxDelta)
        {
            if (!Mathf.Approximately(Time.maximumDeltaTime, maxDelta))
                Time.maximumDeltaTime = maxDelta;
            foreach (var world in Unity.Entities.World.All)
                if (world.IsCreated && !Mathf.Approximately(world.MaximumDeltaTime, maxDelta))
                    world.MaximumDeltaTime = maxDelta;
        }

        private void Measure()
        {
            var real = Time.unscaledDeltaTime;
            if (real <= 0f)
                return;
            // Smooth over roughly half a second so the readout is steady.
            var k = Mathf.Clamp01(real * 2f);
            _measured = Mathf.Lerp(_measured, Time.deltaTime / real, k);
            Actual = _measured;
            Fps = Mathf.Lerp(Fps, 1f / real, k);
        }

        private static void Apply(float scale)
        {
            Time.timeScale = scale;
            Time.fixedDeltaTime = BaseFixedDeltaTime * scale;
        }
    }
}
