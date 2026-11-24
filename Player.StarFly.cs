using System;
using System.Drawing;
using System.Numerics;

namespace DeskMadeline
{
    internal enum StarFlyParticle { Boost, Flying }

    public partial class Player
    {
        const float StarFlyTime = 2f;
        internal const string StarFlyLoop = "player.starFlyLoop", StarFlyWarning = "player.starFlyWarning";
        internal static readonly Color StarFlyGold = Color.FromArgb(0xff, 0xd6, 0x5c);
        internal static readonly Color FlyPowerHairColor = Color.FromArgb(0xf2, 0xeb, 0x6d);
        float starFlyTimer, starFlySpeedLerp, starFlyWait;
        Vector2 starFlyLastDir;
        StarFlyPhase starFlyPhase;
        enum StarFlyPhase { Transforming, Pause, Flying, Warning }

        internal Color StarFlySpriteColor { get; private set; } = Color.White;
        internal bool StarFlyHasInput { get; private set; }
        internal bool StarFlyOutline => State == StStarFly && starFlyPhase >= StarFlyPhase.Flying;
        internal Action<StarFlyParticle, PointF, float> OnStarFlyParticles { get; set; }

        internal bool StartStarFly()
        {
            Stamina = ClimbMaxStamina;
            if (State == StReflectionFall || !Sprite.Has("startStarFly") || !Sprite.Has("starFly")) return false;
            if (State == StStarFly)
            {
                starFlyTimer = StarFlyTime;
                StarFlySpriteColor = StarFlyGold;
                Rumble(RumbleStrength.Medium, RumbleLength.Medium);
                return true;
            }

            if (State == StDash) CallDashEvents();
            State = StStarFly;
            PlaySprite("startStarFly", true);
            starFlyPhase = StarFlyPhase.Transforming;
            starFlyTimer = StarFlyTime;
            starFlySpeedLerp = 0f;
            jumpGraceTimer = 0f;
            StarFlySpriteColor = Color.White;
            StarFlyHasInput = true;
            PlayKeyedSound("event:/game/06_reflection/feather_state_loop", StarFlyLoop);
            StopKeyedSound(StarFlyWarning);
            return true;
        }

        void StarFlyUpdate(float dt, PetInput input, float time)
        {
            Rumble(RumbleStrength.Climb, RumbleLength.Short);
            Vector2 aim = new Vector2(input.FeatherX, input.FeatherY);
            Vector2 velocity = new Vector2(Speed.X, Speed.Y);
            if (starFlyPhase < StarFlyPhase.Flying)
            {
                float length = velocity.Length();
                velocity = length <= 1000f * dt ? Vector2.Zero : velocity * ((length - 1000f * dt) / length);
                Speed = new PointF(velocity.X, velocity.Y);
                // The coroutine waits for both the sprite transition and zero
                // speed, then yields .1 seconds before starting the flight.
                if (starFlyPhase == StarFlyPhase.Transforming)
                {
                    if (Sprite.CurrentAnimationID != "startStarFly" && Speed.IsEmpty)
                    {
                        starFlyPhase = StarFlyPhase.Pause;
                        starFlyWait = .1f;
                    }
                    return;
                }
                if (starFlyWait > 0f) { starFlyWait -= dt; return; }
                if (aim == Vector2.Zero) aim = new Vector2(Facing, 0);
                starFlyLastDir = aim;
                Speed = new PointF(aim.X * 250f, aim.Y * 250f);
                StarFlySpriteColor = StarFlyGold;
                HairCount = 7;
                starFlyTimer = StarFlyTime;
                starFlyPhase = StarFlyPhase.Flying;
                RefillDash();
                Stamina = ClimbMaxStamina;
                EmitStarFly(StarFlyParticle.Boost, -aim.X, -aim.Y);
                Rumble(RumbleStrength.Strong, RumbleLength.Medium);
                return;
            }

            StarFlyHasInput = aim != Vector2.Zero;
            Vector2 target = StarFlyHasInput ? aim : starFlyLastDir;
            Vector2 direction = target;
            if (velocity != Vector2.Zero)
            {
                float angle = MathF.Atan2(velocity.Y, velocity.X);
                float delta = MathF.IEEERemainder(MathF.Atan2(target.Y, target.X) - angle, MathF.Tau);
                angle += Math.Clamp(delta, -5.5850534f * dt, 5.5850534f * dt);
                direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            }
            starFlyLastDir = direction;
            float desiredSpeed;
            if (!StarFlyHasInput)
            {
                starFlySpeedLerp = 0f;
                desiredSpeed = 91f;
            }
            else if (direction != Vector2.Zero && Vector2.Dot(direction, aim) >= .45f)
            {
                starFlySpeedLerp = Approach(starFlySpeedLerp, 1f, dt);
                desiredSpeed = 140f + (190f - 140f) * starFlySpeedLerp;
            }
            else
            {
                starFlySpeedLerp = 0f;
                desiredSpeed = 140f;
            }
            float newSpeed = Approach(velocity.Length(), desiredSpeed, 1000f * dt);
            Speed = new PointF(direction.X * newSpeed, direction.Y * newSpeed);
            if (StarFlyInterval(time, dt, .02f)) EmitStarFly(StarFlyParticle.Flying, -Speed.X, -Speed.Y);

            if (input.JumpPressed)
            {
                if (CollideAt(Pos.X, Pos.Y + 3f) || CrossesJumpThru(Pos.X, Pos.Y, Pos.Y + 3f, out _))
                {
                    Jump(input);
                    EndStarFly(StNormal);
                    return;
                }
                if (WallJumpCheck(-1)) { WallJump(1, input); EndStarFly(StNormal); return; }
                if (WallJumpCheck(1)) { WallJump(-1, input); EndStarFly(StNormal); return; }
            }
            if (input.GrabHeld)
            {
                int wall = input.MoveX != -1 && ClimbCheck(1) ? 1 : input.MoveX != 1 && ClimbCheck(-1) ? -1 : 0;
                if (wall != 0)
                {
                    Facing = wall;
                    EndStarFly(StClimb);
                    EnterClimb();
                    return;
                }
            }
            if (CanDash)
            {
                EndStarFly(StDash);
                State = StartDash(input);
                return;
            }

            starFlyTimer -= dt;
            if (starFlyTimer <= 0f)
            {
                if (input.MoveY == -1) Speed.Y = -100f;
                if (input.MoveY < 1)
                {
                    varJumpSpeed = Speed.Y;
                    autoJump = true;
                    varJumpTimer = .2f;
                }
                Speed.Y = Math.Min(Speed.Y, 0f);
                Speed.X = Math.Clamp(Speed.X, -140f, 140f);
                Rumble(RumbleStrength.Medium, RumbleLength.Medium);
                EndStarFly(StNormal);
                return;
            }
            if (starFlyTimer < .5f && StarFlyInterval(time, dt, .05f))
                StarFlySpriteColor = StarFlySpriteColor == StarFlyGold ? NormalHairColor : StarFlyGold;
            if (starFlyTimer <= .5f && starFlyPhase == StarFlyPhase.Flying)
            {
                starFlyPhase = StarFlyPhase.Warning;
                PlayKeyedSound("event:/game/06_reflection/feather_state_warning", StarFlyWarning);
            }
        }

        static bool StarFlyInterval(float time, float dt, float interval)
            => (int)((time - dt) / interval) < (int)(time / interval);

        void EmitStarFly(StarFlyParticle kind, float x, float y)
        {
            if (!Ghost) OnStarFlyParticles?.Invoke(kind, Center, MathF.Atan2(y, x));
        }

        void StarFlyBump(bool horizontal)
        {
            float multiplier = starFlyTimer < .2f ? 0f : -.5f;
            if (horizontal) Speed.X *= multiplier;
            else Speed.Y *= multiplier;
            if (multiplier == 0f) return;
            PlaySound("event:/game/06_reflection/feather_state_bump");
            Rumble(RumbleStrength.Light, RumbleLength.Medium);
        }

        void ClearStarFlyAudio()
        {
            StopKeyedSound(StarFlyLoop);
            StopKeyedSound(StarFlyWarning);
        }

        void EndStarFly(int state)
        {
            PlaySound("event:/game/06_reflection/feather_state_end");
            ClearStarFlyAudio();
            StarFlySpriteColor = Color.White;
            State = state;
            HairCount = Dashes > 1 ? 5 : 4;
            Ducking = false;
            if (CollideAt(Pos.X, Pos.Y))
            {
                Pos.Y -= 2f;
                if (CollideAt(Pos.X, Pos.Y))
                {
                    Ducking = true;
                    // Window borders can move into the player during flight.
                    // Use the normal desktop displacement for that case.
                    DisplaceOutOfSolids();
                }
            }
            if (state != StDash) EmitStarFly(StarFlyParticle.Boost, -Speed.X, -Speed.Y);
        }

        internal void SuperBounce(float top)
        {
            if (State == StStarFly) EndStarFly(StNormal);
            bool ducked = Ducking;
            Ducking = false;
            MoveV(top - Pos.Y);
            RefillDash();
            Stamina = ClimbMaxStamina;
            if (State == StDash) CallDashEvents();
            State = StNormal;
            jumpGraceTimer = 0f;
            varJumpTimer = .2f;
            autoJump = true;
            dashAttackTimer = gliderBoostTimer = wallBoostTimer = 0f;
            wallSlideTimer = WallSlideTime;
            varJumpSpeed = -185f;
            Speed = new PointF(0f, varJumpSpeed);
            Rumble(RumbleStrength.Medium, RumbleLength.Medium);
            SpriteScaleX = .5f;
            SpriteScaleY = 1.5f;
            Ducking = ducked;
        }
    }
}
