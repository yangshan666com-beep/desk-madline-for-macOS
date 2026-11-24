using System;
using System.Drawing;
using System.Numerics;

namespace DeskMadeline
{
    internal interface IBooster
    {
        PointF Center { get; }
        bool Red { get; }
        void Launch(Player player, PointF direction);
        void Release(Player player);
    }

    public partial class Player
    {
        IBooster currentBooster, activeBooster;
        float boostWait, hitSquashNoMoveTimer;
        bool boostCoroutineStarted;

        internal bool EnterBooster(IBooster booster)
        {
            if (Ghost || IsDead || IsRespawning || BeingDragged || State == StBoost) return false;
            if (State == StDash || State == StRedDash) CallDashEvents();
            ClearBoosters();
            if (State == StStarFly) EndStarFly(StBoost);
            else State = StBoost;
            if (Holding != null) DropGlider();
            RefillDash();
            Stamina = ClimbMaxStamina;
            currentBooster = booster;
            boostWait = .25f;
            boostCoroutineStarted = false;
            Speed = PointF.Empty;
            return true;
        }

        internal void CancelBooster(IBooster booster)
        {
            if (currentBooster != booster && activeBooster != booster) return;
            bool mustExit = State == StBoost || State == StRedDash;
            ClearBoosters();
            if (mustExit)
            {
                dashAimPending = false;
                dashAttackTimer = 0f;
                if (State == StBoost) Speed = PointF.Empty;
                EnterNormal();
            }
        }

        void ClearBoosters()
        {
            IBooster waiting = currentBooster, launched = activeBooster;
            currentBooster = activeBooster = null;
            if (Ghost) return;
            waiting?.Release(this);
            if (launched != waiting) launched?.Release(this);
        }

        void UpdateBoosterLinks()
        {
            if (State != StBoost && State != StDash && State != StRedDash &&
                (currentBooster != null || activeBooster != null))
                ClearBoosters();
        }

        bool LaunchCurrentBooster()
        {
            if (activeBooster != null)
            {
                activeBooster.Release(this);
                activeBooster = null;
            }
            if (currentBooster == null) return false;
            activeBooster = currentBooster;
            currentBooster = null;
            activeBooster.Launch(this, DashDir);
            return true;
        }

        void BoostUpdate(float dt, PetInput input)
        {
            if (currentBooster == null) { EnterNormal(); return; }
            PointF target = currentBooster.Center;
            PointF center = Center;
            var aim = new Vector2(input.FeatherX, input.FeatherY);
            Vector2 toward = new Vector2(target.X - center.X - counter.X, target.Y - center.Y - counter.Y) + aim * 3f;
            float length = toward.Length();
            if (length > 80f * dt) toward *= 80f * dt / length;
            MoveH(toward.X);
            MoveV(toward.Y);

            bool pressed = HasDashBuffer;
            if (!pressed)
            {
                // The first step of BoostCoroutine yields .25s. Monocle checks
                // the wait before decrementing it on subsequent scene updates.
                if (!boostCoroutineStarted) { boostCoroutineStarted = true; return; }
                if (boostWait > 0f) { boostWait -= dt; return; }
            }

            bool crouchDash = pressed && crouchDashBufferTimer > 0f;
            ConsumeDash();
            center = Center;
            MoveH(MathF.Floor(target.X - center.X) - counter.X);
            MoveV(MathF.Floor(target.Y - center.Y) - counter.Y);
            LastDashWasTwo = Dashes == 2;
            startedDashingCount = Dashes;
            if (!currentBooster.Red)
            {
                // BoostEnd transitions straight to DashBegin without StartDash,
                // so the bubble's launch does not spend the dash it refilled.
                State = BeginDash(input, crouchDash);
                return;
            }

            calledDashEvents = false;
            dashStartedOnGround = false;
            ApplyFreeze(.05f);
            dashCooldownTimer = .2f;
            dashRefillCooldownTimer = .1f;
            StartedDashing = true;
            DashSequenceCount++;
            dashAttackTimer = .3f;
            gliderBoostTimer = .55f;
            DashDir = Speed = PointF.Empty;
            dashAimPending = true;
            State = StRedDash;
            if (!onGround && CanUnDuck) Ducking = false;
            Rumble(RumbleStrength.Strong, RumbleLength.Medium);
        }

        void ApplyRedDashAim()
        {
            DashDir = gliderBoostDir = lastAim;
            Speed = new PointF(lastAim.X * DashSpeed, lastAim.Y * DashSpeed);
            if (DashDir.X != 0f) Facing = Sign(DashDir.X);
            CallDashEvents();
        }

        void RedDashUpdate(PetInput input)
        {
            StartedDashing = false;
            gliderBoostTimer = .05f;
            if (CanDash)
            {
                CallDashEvents();
                State = StartDash(input);
                return;
            }
            if (DashDir.Y == 0f)
            {
                foreach (Solid platform in JumpThrus)
                    if (OverlapsHitbox(platform) && CollisionBottom - platform.T <= 6f)
                        MoveVExact((int)(platform.T - CollisionBottom));
                if (CanUnDuck && input.JumpPressed && jumpGraceTimer > 0f)
                {
                    SuperJump();
                    EnterNormal();
                    return;
                }
            }
            if (!input.JumpPressed || !CanUnDuck) return;
            for (int wall = 1; wall >= -1; wall -= 2)
            {
                if (!WallJumpCheck(wall)) continue;
                if (DashDir.X == 0f && DashDir.Y == -1f) SuperWallJump(-wall);
                else if (Facing == wall && input.GrabHeld && Stamina > 0f && Holding == null)
                    ClimbJump(input);
                else WallJump(-wall, input);
                EnterNormal();
                return;
            }
        }

        void RedDashHit()
        {
            CallDashEvents();
            State = StHitSquash;
            hitSquashNoMoveTimer = .1f;
            Rumble(RumbleStrength.Medium, RumbleLength.Short);
        }

        void HitSquashUpdate(float dt, PetInput input)
        {
            Speed.X = Approach(Speed.X, 0f, 800f * dt);
            if (input.JumpPressed)
            {
                if (onGround) Jump(input);
                else if (WallJumpCheck(1)) WallJump(-1, input);
                else if (WallJumpCheck(-1)) WallJump(1, input);
                else ConsumeJump();
                EnterNormal();
                return;
            }
            if (CanDash) { State = StartDash(input); return; }
            if (input.GrabHeld && ClimbCheck(Facing)) { EnterClimb(); State = StClimb; return; }
            if (hitSquashNoMoveTimer > 0f) hitSquashNoMoveTimer -= dt;
            else EnterNormal();
        }
    }
}
