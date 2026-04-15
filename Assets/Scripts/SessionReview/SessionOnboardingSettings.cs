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
        public static SEAN.Scenario.Agents.PwdGender SelectedPwdGender { get; private set; } = SEAN.Scenario.Agents.PwdGender.Male;
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
            SelectedPwdGender = SEAN.Scenario.Agents.PwdGender.Male;
            SelectedSceneIndex = -1;
            SelectedSceneName = string.Empty;
            PendingTrialStart = false;
        }

        public static void Apply(OnboardingPlayerMode playerMode,
                                 SEAN.Scenario.Agents.PwdGender pwdGender,
                                 int sceneIndex,
                                 string sceneName,
                                 StartupControlMode robotStartupControl,
                                 StartupControlMode pwdStartupControl)
        {
            HasCompletedOnboarding = true;
            PlayerMode = playerMode;
            RobotStartupControl = robotStartupControl;
            PwdStartupControl = pwdStartupControl;
            SelectedPwdGender = pwdGender;
            SelectedSceneIndex = sceneIndex;
            SelectedSceneName = sceneName ?? string.Empty;
            PendingTrialStart = true;
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

        public static void MarkTrialStarted()
        {
            PendingTrialStart = false;
        }
    }
}
