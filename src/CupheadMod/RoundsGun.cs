using System.Collections.Generic;
using UnityEngine;

namespace CupRounds
{
    /// <summary>
    /// The ROUNDS gun. Shots are Cuphead's own player projectile (so boss damage, hit pauses and scoring all work),
    /// driven by RoundsBullet with ROUNDS motion and drawn with the ROUNDS bullet.
    /// </summary>
    public class RoundsGun
    {
        private readonly BlobController c;
        private AbstractProjectile prefab;
        private float ammo;
        private float reloadLeft = -1f;
        private float sinceShot = 10f;
        private float charge;
        private bool wasHeld;
        private readonly List<float> burstTimers = new List<float>();
        private float burstCharge = 1f;

        public RoundsGun(BlobController c)
        {
            this.c = c;
            ammo = Mathf.Max(1, c.build.maxAmmo);
        }

        public int Ammo => reloadLeft >= 0f ? 0 : Mathf.FloorToInt(ammo);
        public int MaxAmmo => Mathf.Clamp(c.build.maxAmmo, 1, 90);

        /// <summary>0..1 while reloading, -1 otherwise.</summary>
        public float ReloadProgress => reloadLeft >= 0f ? 1f - reloadLeft / Mathf.Max(0.01f, ReloadTime()) : -1f;

        public void ApplyBuild()
        {
            ammo = Mathf.Min(ammo, MaxAmmo);
            if (Level.Current == null || Level.Current.LevelTime < 2f) ammo = MaxAmmo;
        }

        public float ReloadTime()
        {
            return Mathf.Max(0.1f, (Tuning.baseReloadTime + c.build.reloadTimeAdd) * c.build.reloadTimeMultiplier);
        }

        public float ShotCooldown()
        {
            float cd = c.build.lockGunToDefault && c.build.forceSpecificAttackSpeed > 0f ? c.build.forceSpecificAttackSpeed : c.build.attackSpeed;
            return Mathf.Max(Tuning.minShotCooldown, cd);
        }

        public float BulletDamage(float chargeMult)
        {
            float d = Tuning.baseBulletDamage * c.build.damage * c.build.bulletDamageMultiplier * Tuning.damageScale * chargeMult;
            d += c.build.percentageDamage * Tuning.percentDamageFlat;
            return Mathf.Max(0.5f, d);
        }

        public void Update(float dt)
        {
            sinceShot += dt;
            UpdateAmmo(dt);
            for (int i = burstTimers.Count - 1; i >= 0; i--)
            {
                burstTimers[i] -= dt;
                if (burstTimers[i] <= 0f)
                {
                    burstTimers.RemoveAt(i);
                    FireVolley(burstCharge);
                }
            }
            bool held = c.input.ShootHeld();
            bool pressed = held && !wasHeld;
            wasHeld = held;
            if (c.build.useCharge)
            {
                UpdateCharge(dt, held);
                return;
            }
            bool wants = c.build.dontAllowAutoFire ? pressed : held;
            if (wants && sinceShot >= ShotCooldown() && reloadLeft < 0f && ammo >= 1f)
                FireShot(1f);
        }

        public void UpdateCharge(float dt, bool held)
        {
            if (held && reloadLeft < 0f && ammo >= 1f)
            {
                charge = Mathf.Min(1f, charge + dt / Tuning.chargeTime);
                return;
            }
            if (charge > 0f)
            {
                float mult = 1f + charge * (c.build.chargeDamageMultiplier - 1f);
                charge = 0f;
                if (sinceShot >= ShotCooldown()) FireShot(Mathf.Max(0.25f, mult));
            }
        }

        public void UpdateAmmo(float dt)
        {
            if (reloadLeft >= 0f)
            {
                reloadLeft -= dt;
                if (reloadLeft < 0f) { reloadLeft = -1f; ammo = MaxAmmo; }
                return;
            }
            if (c.build.ammoReg > 0f && ammo < MaxAmmo)
                ammo = Mathf.Min(MaxAmmo, ammo + c.build.ammoReg * dt * MaxAmmo);
            if (ammo < 1f) { reloadLeft = ReloadTime(); return; }
            if (ammo < MaxAmmo && sinceShot >= Tuning.idleReloadDelay + ShotCooldown() && !c.input.ShootHeld())
                reloadLeft = ReloadTime();
        }

        public void FireShot(float chargeMult)
        {
            ammo -= 1f;
            sinceShot = 0f;
            burstCharge = chargeMult;
            int bursts = Mathf.Clamp(c.build.bursts, 1, 20);
            FireVolley(chargeMult);
            for (int i = 1; i < bursts; i++)
                burstTimers.Add(i * Mathf.Max(0.03f, c.build.timeBetweenBullets));
            c.body.ApplyRecoil();
            c.body.MarkShot();
            AudioManager.Play("player_weapon_peashot_start");
        }

        private void FireVolley(float chargeMult)
        {
            AbstractProjectile p = Prefab();
            if (p == null) return;
            int n = Mathf.Clamp(c.build.numberOfProjectiles, 1, (int)Tuning.maxProjectilesPerShot);
            float cone = Mathf.Clamp(c.build.spread * c.build.multiplySpread, 0f, 2f) * Tuning.spreadDegreesPerUnit;
            if (n > 1 && cone < 4f * n) cone = Mathf.Max(cone, 4f * n);
            Vector2 aim = c.body.AimDir;
            float baseAngle = Mathf.Atan2(aim.y, aim.x) * Mathf.Rad2Deg;
            float dmg = BulletDamage(chargeMult);
            for (int i = 0; i < n; i++)
            {
                float even = n > 1 ? Mathf.Lerp(-cone / 2f, cone / 2f, i / (float)(n - 1)) : 0f;
                float rand = Random.Range(-cone / 2f, cone / 2f);
                float angle = baseAngle + Mathf.Lerp(rand, even, Mathf.Clamp01(c.build.evenSpread + (n > 1 ? 0.5f : 0f)));
                float speedJitter = n > 1 ? Random.Range(0.9f, 1.1f) : 1f;
                SpawnBullet(p, angle, dmg, speedJitter);
            }
        }

        private void SpawnBullet(AbstractProjectile p, float angle, float dmg, float speedJitter)
        {
            Vector2 muzzle = c.body.Muzzle;
            var proj = p.Create(muzzle, angle) as BasicProjectile;
            if (proj == null) return;
            proj.Speed = 0f;
            proj.Gravity = 0f;
            proj.Damage = dmg;
            proj.PlayerId = c.player.id;
            proj.DamagesType.PlayerProjectileDefault();
            proj.CollisionDeath.PlayerProjectileDefault();
            var rb = proj.gameObject.AddComponent<RoundsBullet>();
            float rad = angle * Mathf.Deg2Rad;
            float speed = Tuning.baseBulletSpeed * c.build.projectileSpeed * Mathf.Clamp(c.build.projectielSimulatonSpeed, 0.3f, 3f) * speedJitter;
            rb.Init(c, proj, new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * Mathf.Clamp(speed, 300f, 6000f), dmg);
            int who = c.index;
            BlobController owner = c;
            proj.OnDealDamageEvent += (d, receiver, dealer) =>
            {
                if (owner != null) owner.health.OnDealtDamage(d);
                TestMode.OnBlobHit(who, d, receiver);
            };
        }

        private AbstractProjectile Prefab()
        {
            if (prefab != null) return prefab;
            object prefabs = Reflect.Get(c.player.weaponManager, "weaponPrefabs");
            object peashot = Reflect.Get(prefabs, "peashot");
            prefab = Reflect.Get<AbstractProjectile>(peashot, "basicPrefab");
            if (prefab == null)
            {
                // Any of the player's weapons will do as a carrier for the ROUNDS bullet.
                foreach (string w in new[] { "spread", "homing", "charge", "bouncer", "crackshot" })
                {
                    prefab = Reflect.Get<AbstractProjectile>(Reflect.Get(prefabs, w), "basicPrefab");
                    if (prefab is BasicProjectile) break;
                }
            }
            if (prefab == null) Plugin.L.LogError("no Cuphead projectile prefab found for the ROUNDS gun");
            return prefab;
        }
    }
}
