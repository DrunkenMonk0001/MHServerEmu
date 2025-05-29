using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Games.Missions.Conditions
{
    public class MissionConditionTeamUpIsActive : MissionPlayerCondition
    {
        public MissionConditionTeamUpIsActive(Mission mission, IMissionConditionOwner owner, MissionConditionPrototype prototype) 
            : base(mission, owner, prototype)
        {
            // NotInGame BenjaTeamUpTest
        }
    }
}
