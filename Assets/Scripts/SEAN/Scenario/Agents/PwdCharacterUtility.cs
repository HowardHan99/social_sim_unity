namespace SEAN.Scenario.Agents
{
    public static class PwdCharacterUtility
    {
        public static bool UsesWheelchairPlayerAnimation(PwdCharacter character)
        {
            return character == PwdCharacter.MaleWheelchairUser
                || character == PwdCharacter.FemaleWheelchairUser;
        }
    }
}
