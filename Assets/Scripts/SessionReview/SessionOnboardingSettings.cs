using UnityEngine;

namespace SessionReview
{
    public enum OnboardingPlayerMode
    {
        Robot,
        Human
    }

    public enum StartupControlMode
    {
        Auto,
        Manual
    }

    public static class SessionOnboardingSettings
    {
        public static bool HasCompletedOnboarding { get; private set; }
        public static OnboardingPlayerMode PlayerMode { get; private set; } = OnboardingPlayerMode.Robot;
        public static StartupControlMode RobotStartupControl { get; private set; } = StartupControlMode.Manual;
        public static StartupControlMode PwdStartupControl { get; private set; } = StartupControlMode.Auto;
        public static SEAN.Scenario.Agents.PwdCharacter SelectedPwdCharacter { get; private set; } = SEAN.Scenario.Agents.PwdCharacter.FemaleWheelchair;
        public static int SelectedSceneIndex { get; private set; } = -1;
        public static string SelectedSceneName { get; private set; } = string.Empty;
        public static bool PendingTrialStart { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            HasCompletedOnboarding = false;
            PlayerMode = OnboardingPlayerMode.Robot;
            RobotStartupControl = StartupControlMode.Manual;
            PwdStartupControl = StartupControlMode.Auto;
            SelectedPwdCharacter = SEAN.Scenario.Agents.PwdCharacter.FemaleWheelchair;
            SelectedSceneIndex = -1;
            SelectedSceneName = string.Empty;
            PendingTrialStart = false;
        }

        public static void Apply(OnboardingPlayerMode playerMode,
                                 SEAN.Scenario.Agents.PwdCharacter pwdCharacter,
                                 int sceneIndex,
                                 string sceneName,
                                 StartupControlMode robotStartupControl,
                                 StartupControlMode pwdStartupControl)
        {
            HasCompletedOnboarding = true;
            PlayerMode = playerMode;
            RobotStartupControl = robotStartupControl;
            PwdStartupControl = pwdStartupControl;
            SelectedPwdCharacter = pwdCharacter;
            SelectedSceneIndex = sceneIndex;
            SelectedSceneName = sceneName ?? string.Empty;
            PendingTrialStart = true;
            Debug.Log("SelectedPwdCharacter in Apply: " + SelectedPwdCharacter);
            Debug.Log("HasCompletedOnboarding" + HasCompletedOnboarding);
        }

        public static void UpdatePwdCharacter(SEAN.Scenario.Agents.PwdCharacter pwdCharacter)
        {
            SelectedPwdCharacter = pwdCharacter;
        }

        public static void UpdateStartupControls(OnboardingPlayerMode playerMode,
                                                 StartupControlMode robotStartupControl,
                                                 StartupControlMode pwdStartupControl)
        {
            PlayerMode = playerMode;
            RobotStartupControl = robotStartupControl;
            PwdStartupControl = pwdStartupControl;
            PendingTrialStart = true;
        }

        /// <summary>
        /// Updates PlayerMode and startup modes for camera/UI while the trial-start prompt or warmup runs.
        /// Does not change PendingTrialStart or HasCompletedOnboarding.
        /// </summary>
        public static void SyncInFlightTrialControls(OnboardingPlayerMode playerMode,
                                                     StartupControlMode robotStartupControl,
                                                     StartupControlMode pwdStartupControl)
        {
            PlayerMode = playerMode;
            RobotStartupControl = robotStartupControl;
            PwdStartupControl = pwdStartupControl;
        }

        public static void MarkTrialStarted()
        {
            PendingTrialStart = false;
        }
    }
}
