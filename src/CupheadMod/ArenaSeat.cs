using System.Collections.Generic;
using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// A Cuphead player that ROUNDS's real player stands in for while the arena is live: a ground level's
    /// <see cref="BlobController"/> (the Cuphead player follows ROUNDS's player) or a plane level's <see cref="PlaneBlob"/>
    /// (Flying: ROUNDS's player is held where the plane flies). Either way ROUNDS's gun, bullets, block, health and cards
    /// are the player's, and the level takes part in the card loop.
    /// </summary>
    public abstract class ArenaSeat : MonoBehaviour
    {
        public static readonly List<ArenaSeat> Seats = new List<ArenaSeat>();

        public int index;
        public BlobInput input;
        private int announcedFor;

        /// <summary>The Cuphead player object (enemy aim, hits, camera, co-op).</summary>
        public abstract AbstractPlayerController Body { get; }

        /// <summary>The player may act: alive, the level under way, not ending, not paused.</summary>
        public abstract bool CanAct { get; }

        /// <summary>Plane levels: Cuphead flies the plane and ROUNDS's player is held at it (no ROUNDS movement or gravity).</summary>
        public virtual bool Flying => false;

        /// <summary>The aim with no mouse or stick input.</summary>
        public virtual Vector2 RestAim => Vector2.right;

        /// <summary>ROUNDS's real player stands in for this one (the arena link is live).</summary>
        public bool Arena => ArenaClient.Drives(this);

        public static bool AnyFlying
        {
            get
            {
                foreach (var s in Seats) if (s != null && s.Flying) return true;
                return false;
            }
        }

        public static ArenaSeat For(AbstractPlayerController p)
        {
            if (p == null) return null;
            foreach (var s in Seats) if (s != null && s.Body == p) return s;
            return null;
        }

        public static ArenaSeat For(Component c)
        {
            return c == null ? null : For(c.GetComponent<AbstractPlayerController>());
        }

        public static ArenaSeat InSlot(int slot)
        {
            foreach (var s in Seats) if (s != null && s.index == slot) return s;
            return null;
        }

        protected void TakeSeat()
        {
            input = new BlobInput(this);
            Seats.Add(this);
        }

        /// <summary>ROUNDS's player arrived (level start, ROUNDS ready late, or ROUNDS started again): it gets this player's
        /// life and deck.</summary>
        protected void Announce()
        {
            int rounds = ArenaClient.RoundsPid;
            if (ArenaClient.Live && announcedFor != rounds && ArenaClient.link.I(ArenaLink.RSlot(index) + ArenaLink.Q_Present) != 0)
            {
                announcedFor = rounds;
                ArenaClient.OnPlayerSpawned(this);
            }
        }

        protected virtual void OnDestroy()
        {
            Seats.Remove(this);
        }
    }
}
