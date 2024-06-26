﻿using MHServerEmu.Core.VectorMath;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Properties;

namespace MHServerEmu.Games.Entities
{
    public class Missile : Agent
    {
        public override Bounds EntityCollideBounds { get; set; }
        public override bool CanRepulseOthers => false;

        private MissileCreationContextPrototype _contextPrototype = null;
        public MissileCreationContextPrototype MissileCreationContextPrototype { get => _contextPrototype; }
        public Random Random { get; private set; }
        public bool IsMovedIndependentlyOnClient { get; internal set; }

        public Missile(Game game) : base(game) 
        { 
            Random = new();
        }

        public override bool CanCollideWith(WorldEntity other)
        {
            if (base.CanCollideWith(other) == false) return false;
            if (other.Properties[PropertyEnum.NoMissileCollide] == true) return false;
            return true;
        }

        internal bool OnBounce(Vector3 position)
        {
            throw new NotImplementedException();
        }
    }
}
