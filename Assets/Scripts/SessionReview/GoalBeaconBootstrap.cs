using UnityEngine;

namespace SessionReview
{
    /// <summary>
    /// Keeps <see cref="GoalBeacon.DiscoverGoals"/> running for the lifetime of the process, so
    /// goal markers that only exist at runtime still get their label: the scenario scenes build
    /// their player/robot goal markers in SEAN.Tasks.Base.initStartAndGoal and re-activate them
    /// between trials, long after the scene finished loading.
    ///
    /// Its own file (rather than a second class inside GoalBeacon.cs) so Unity can always
    /// resolve the type for AddComponent.
    /// </summary>
    internal class GoalBeaconBootstrap : MonoBehaviour
    {
        private float nextDiscoverTime = float.NegativeInfinity;

        void Update()
        {
            // Unscaled: review freezes Time.timeScale, and the labels must keep appearing there.
            if (Time.unscaledTime < nextDiscoverTime) return;
            nextDiscoverTime = Time.unscaledTime + GoalBeacon.DiscoverInterval;
            GoalBeacon.DiscoverGoals();
        }
    }
}
