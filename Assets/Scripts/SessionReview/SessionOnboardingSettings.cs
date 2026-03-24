using UnityEngine;

namespace SessionReview
{
    public enum OnboardingPlayerMode
    {
        Robot,
        Human
    }

    public static class SessionOnboardingSettings
    {
        public static bool HasCompletedOnboarding { get; private set; }
        public static OnboardingPlayerMode PlayerMode { get; private set; } = OnboardingPlayerMode.Robot;
        public static SEAN.Scenario.Agents.PwdGender SelectedPwdGender { get; private set; } = SEAN.Scenario.Agents.PwdGender.Male;
        public static int SelectedSceneIndex { get; private set; } = -1;
        public static string SelectedSceneName { get; private set; } = string.Empty;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            HasCompletedOnboarding = false;
            PlayerMode = OnboardingPlayerMode.Robot;
            SelectedPwdGender = SEAN.Scenario.Agents.PwdGender.Male;
            SelectedSceneIndex = -1;
            SelectedSceneName = string.Empty;
        }

        public static void Apply(OnboardingPlayerMode playerMode,
                                 SEAN.Scenario.Agents.PwdGender pwdGender,
                                 int sceneIndex,
                                 string sceneName)
        {
            HasCompletedOnboarding = true;
            PlayerMode = playerMode;
            SelectedPwdGender = pwdGender;
            SelectedSceneIndex = sceneIndex;
            SelectedSceneName = sceneName ?? string.Empty;
        }
    }
}
