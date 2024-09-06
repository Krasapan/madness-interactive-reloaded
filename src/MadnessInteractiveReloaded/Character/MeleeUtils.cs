﻿using System;
using System.Numerics;
using Walgelijk;
using Walgelijk.Physics;

namespace MIR;

/// <summary>
/// Static singleton for 
/// </summary>
public static class MeleeUtils
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="stats"></param>
    /// <returns>The scaled melee animation speed based on their <see cref="CharacterStats.MeleeSkill"/></returns>
    public static float GetMeleeAnimationSpeedFor(CharacterStats stats) => float.Clamp(stats.MeleeSkill * 1.5f + 0.5f, 1, 3) / stats.Scale;

    /// <summary>
    /// Attempt to perform a melee attack with the given parameters.
    /// </summary>
    /// <exception cref="Exception"></exception>
    public static void TryPerformMeleeAttack(Scene scene, WeaponComponent? equipped, CharacterComponent character)
    {
        if (character.AnimationConstrainsAny(AnimationConstraint.PreventMelee))
            return;

        var speed = GetMeleeAnimationSpeedFor(character.Stats);
        character.AnimationFlipFlop++;

        if (scene.TryGetComponentFrom<MeleeSequenceComponent>(character.Entity, out var meleeSequenceComponent))
            meleeSequenceComponent.CanContinue = true;
        else
        {
            string meleeSeq = character.Stats.UnarmedSeq;
            if (equipped != null)
                switch (equipped.Data.MeleeDamageType)
                {
                    case MeleeDamageType.Blade:
                        meleeSeq = character.Stats.SwordSeq;
                        break;
                    case MeleeDamageType.Axe:
                        meleeSeq = character.Stats.BluntSeq;
                        break;
                    case MeleeDamageType.Firearm:
                        meleeSeq = equipped.HoldPoints.Length == 2 ? character.Stats.TwoHandedGunSeq : character.Stats.OneHandedGunSeq;
                        break;
                    case MeleeDamageType.Blunt:
                        meleeSeq = equipped.HoldPoints.Length == 2 ? character.Stats.TwoHandedSeq : character.Stats.BluntSeq;
                        break;
                }

            var a = new MeleeSequenceComponent(Registries.MeleeSequences.Get(meleeSeq), speed);
            scene.AttachComponent(character.Entity, a);
        }
    }

    /*                                            Welcome to hell
     *                                      
     * This code was written to match the information found in "MIR Melee Corollarium.pdf, which is why
     * it looks like such a mess. If you could rewrite this in cleaner, simpler, shorter way then go ahead.
     * Make sure it stays functional and still follows the document. Especially those matrix diagrams!
     * ---
     * UPDATE: The PDF is obsolete because it wasn't fun to play. Ignore it. 
     * So I'm not sure what this code does now. Good luck lmfao.
     */

    /// <summary>
    /// Run a raycast specifically for melee attacks.
    /// </summary>
    /// <param name="scene"></param>
    /// <param name="actor">The responsible character</param>
    /// <param name="origin">Where the melee attack originates.</param>
    /// <param name="direction">The direction of the attack ray.</param>
    /// <param name="distance">How far the melee ray will go.</param>
    /// <param name="damage">How much damage to inflict.</param>
    /// <param name="targetCollisionLayer">The layer to run the raytrace on.</param>
    /// <param name="weapon">The equipped weapon.</param>
    /// <param name="finalAttack">Is this the final attack in the potential melee sequence?.</param>
    public static void DoMeleeHit(Scene scene, CharacterComponent actor, Vector2 origin, Vector2 direction,
        float distance, float damage, uint targetCollisionLayer,
        WeaponComponent? weapon, bool finalAttack = false)
    {
        //damage = 0;
        distance += 200; // TODO wtf is this lmfao 

        if (!scene.GetSystem<PhysicsSystem>().Raycast(origin, direction, out var hit, distance, targetCollisionLayer | CollisionLayers.BlockPhysics, ignore: actor.AttackIgnoreCollision))
            return;

        var point = hit.Position;
        var hitTransform = scene.GetComponentFrom<TransformComponent>(hit.Entity);
        var localPoint = Vector2.Transform(point, hitTransform.WorldToLocalMatrix);

        if (scene.TryGetComponentFrom<IsMeleeHitTriggerComponent>(hit.Entity, out var trigger))
            trigger.Event.Dispatch(new HitEvent(weapon, hit.Position, hit.Normal, direction));

        var hasBodyPart = scene.TryGetComponentFrom<BodyPartComponent>(hit.Entity, out var bodyPart);

        if (scene.TryGetComponentFrom<ImpactOffsetComponent>(hit.Entity, out var impactOffset))
        {
            if (!hasBodyPart|| bodyPart!.Character.Get(scene).HasFlag(CharacterFlags.AttackResponseMelee))
            {
                float rot = 3 * actor.Stats.MeleeSkill;
                rot *= direction.X > 0 ? 1 : -1;

                if (localPoint.Y > hitTransform.LocalRotationPivot.Y)
                    impactOffset.RotationOffset += -rot;
                else
                    impactOffset.RotationOffset += rot;

                impactOffset.TranslationOffset += direction * 3 * actor.Stats.MeleeSkill;
            }
        }

        if (hasBodyPart)
        {
            var victim = bodyPart!.Character.Get(scene);

            if (!victim.Flags.HasFlag(CharacterFlags.AttackResponseMelee))
                return;

            if (victim.AnimationConstrainsAny(AnimationConstraint.PreventBeingMeleed))
                return;

            var victimIsPlayer = scene.HasTag(victim.Entity, Tags.Player);
            if (victimIsPlayer && ImprobabilityDisks.IsEnabled("god"))
                return;

            // blocking
            if (victim.IsMeleeBlocking && CharacterUtilities.CanDodge(victim))
            {
                if (victim.HasWeaponEquipped && victim.Positioning.MeleeBlockProgress < 1 && !scene.HasTag(actor.Entity, Tags.Player))
                {
                    // perfect block! parry the attack
                    actor.DodgeMeter = 0;
                    actor.DodgeRegenCooldownTimer = 1; // TODO convar
                    if (Utilities.RandomFloat() > actor.Stats.MeleeSkill)
                        actor.DropWeapon(scene);
                    actor.PlayAnimation(Registries.Animations.Get(!actor.Positioning.IsFlipped ? "melee_stun_sword_L" : "melee_stun_sword_R")); // TODO this should be in Animations.cs
                    scene.Game.AudioRenderer.PlayOnce(Sounds.MeleeClash.Parry, new Vector3(point, 0));

                    victim.Positioning.MeleeBlockImpactIntensity += Utilities.RandomFloat(-1, 1);

                    //  Prefabs.CreateDeflectionSpark(scene, hitPosOnLine, Utilities.VectorToAngle(returnDir), 1);
                    return;
                }
                else
                {
                    switch (GetBlockResponse(scene, actor, victim))
                    {
                        case MeleeInteractionResponse.BlockVictim:
                            {
                                victim.DrainDodge(damage * 0.02f); // TODO convar
                                scene.Game.AudioRenderer.PlayOnce(Sounds.MeleeClash.GetClashFor(scene, victim.EquippedWeapon, actor.EquippedWeapon), new Vector3(point, 0));
                                victim.Positioning.MeleeBlockImpactIntensity += Utilities.RandomFloat(-1, 1);
                                return;
                            }
                        case MeleeInteractionResponse.StunVictim:
                            {
                                victim.DrainDodge(damage * 0.1f); // TODO convar
                                scene.Game.AudioRenderer.PlayOnce(Sounds.MeleeClash.GetClashFor(scene, victim.EquippedWeapon, actor.EquippedWeapon), new Vector3(point, 0));
                                victim.PlayAnimation(Registries.Animations.Get(!victim.Positioning.IsFlipped ? "melee_stun_sword_L" : "melee_stun_sword_R")); // TODO this should be in Animations.cs
                                victim.Positioning.MeleeBlockImpactIntensity += Utilities.RandomFloat(-1, 1);
                                return;
                            }
                        default:
                            break;
                    }
                }
            }

            if (victimIsPlayer && actor.HasWeaponEquipped && CharacterUtilities.CanDodge(victim))
            {
                victim.DrainDodge(damage);
                if (victim.DodgeMeter > 0 || victim.Stats.DodgeOversaturate)
                {
                    CharacterUtilities.TryDodgeAnimation(victim);
                    return;
                }
            }

            {
                if (weapon == null && /*victimIsPlayer &&*/ CharacterUtilities.CanDodge(victim))
                    victim.DrainDodge(damage);
                else
                    bodyPart.Damage(damage);

                if (!victimIsPlayer && !finalAttack)  // TODO convar
                {
                    if (victim.DodgeMeter < 0.5f)
                    {
                        if (victim.Positioning.GlobalCenter.X > actor.Positioning.GlobalCenter.X != victim.Positioning.IsFlipped)
                            victim.PlayAnimation(Registries.Animations.Get("stun_light_forwards"));
                        else
                            victim.PlayAnimation(Registries.Animations.Get(Utilities.PickRandom("stun_light_backwards", "stun_light_backwards2")));
                    }
                }
            }

            if (weapon?.Data.MeleeDamageType is MeleeDamageType.Blade or MeleeDamageType.Axe)
            {
                if (scene.TryGetComponentFrom<BodyPartShapeComponent>(hit.Entity, out var damagable))
                {
                    var localPos = localPoint;
                    if (damagable.HorizontalFlip)
                        localPos.X *= -1;

                    localPos.X = float.Lerp(float.Clamp(localPos.X, 0, 1), 0.5f, 0.2f);
                    localPos.Y = float.Lerp(float.Clamp(localPos.Y, 0, 1), 0.5f, 0.2f);

                    damagable.AddSlash(localPos, Utilities.RandomFloat(0, float.Tau));

                    //TODO dismemberment??
                    //TODO stukkies eraf snijden?
                    for (int i = 0; i < Utilities.RandomInt(1, 3); i++)
                        Prefabs.CreateBloodSpurt(scene,
                            hit.Position,
                            float.Atan2(hit.Normal.Y, hit.Normal.X) * Utilities.RadToDeg,
                            damagable.BloodColour, Utilities.Clamp(damage * 4, 1f, 2f));
                }

                scene.Game.AudioRenderer.PlayOnce(Utilities.PickRandom(victim.IsAlive ? Sounds.LivingSwordHit : Sounds.GenericSwordHit), new Vector3(hit.Position, 0));
            }
            else
                scene.Game.AudioRenderer.PlayOnce(Utilities.PickRandom(victim.IsAlive ? Sounds.LivingPunch : Sounds.GenericPunch), new Vector3(hit.Position, 0));

            CharacterUtilities.UpdateAliveStatus(scene, victim);
            if (victim.IsAlive)
            {
                if (!actor.HasWeaponEquipped && !scene.HasTag(victim.Entity, Tags.Player) && finalAttack)
                {
                    if (victim.EquippedWeapon.TryGet(scene, out var victimWep))
                    {
                        if (scene.TryGetComponentFrom<VelocityComponent>(victimWep.Entity, out var vel))
                        {
                            MadnessUtils.Delay(0.01f, () =>
                            {
                                vel.Acceleration += new Vector2(victim.Positioning.IsFlipped ? -1 : 1, 0.5f) * 500;
                                vel.RotationalAcceleration += Utilities.RandomFloat(-1000, 1000);
                            });
                        }
                        victim.DropWeapon(scene);
                    }
                    scene.DetachComponent<MeleeSequenceComponent>(victim.Entity);
                    if (!victim.IsPlayingAnimationGroup("stun")) // TODO this is fucked up??
                    {
                        CharacterUtilities.StunHeavy(scene, victim, true);
                    }
                }
                else
                {
                    // TODO what the fuck
                    if (!victim.IsPlayingAnimation || victim.IsPlayingAnimationGroup(Animations.FistMeleeHits[0].Group)) // TODO find way to get animation group without doing... this
                        victim.PlayAnimation(Utilities.PickRandom(Animations.FistMeleeHits)); //isBlade ? Animations.FistMeleeHits : Animations.SwordMeleeHits));
                }
            }
            else
            {
                // increment player kills stat
                if (scene.FindAnyComponent<GameModeComponent>(out var mode) && mode.Mode == GameMode.Campaign)
                    if (scene.HasTag(actor.Entity, Tags.Player) && Level.CurrentLevel != null)
                        CampaignProgress.GetCurrentStats()?.IncrementKills(Level.CurrentLevel);

                if (!victim.AnimationConstrainsAny(AnimationConstraint.PreventRagdoll))
                {
                    var addedVelocity = actor.AimDirection * float.Max(0.5f, actor.Stats.MeleeKnockback);
                    addedVelocity.Y *= 1.5f;

                    var handVel = actor.Positioning.Hands.First.PreviousAnimatedPosition - actor.Positioning.Hands.First.PreviousAnimatedPosition;

                    addedVelocity += handVel * 2;
                    MadnessUtils.TurnIntoRagdoll(scene, victim, addedVelocity);
                }
            }
        }
    }


    public enum MeleeInteractionResponse
    {
        Invalid,
        Unobstructed,
        BlockVictim,
        StunVictim
    }

    /// <summary>
    /// Returns a <see cref="MeleeInteractionResponse"/> based on the two given weapons, following the matrix diagram in "MIR Melee Corollarium.pdf"
    /// </summary>
    private static MeleeInteractionResponse GetBlockResponse(Scene scene, CharacterComponent actor, CharacterComponent victim)
    {
        var victimArmed = victim.EquippedWeapon.TryGet(scene, out var victimWep);
        var actorArmed = actor.EquippedWeapon.TryGet(scene, out var actorWep);

        if (victimArmed && actorArmed)
            return GetInteractionResponse(actorWep!.Data, victimWep!.Data);

        // weapon doesnt exist, so either one or both are unarmed

        if (victimArmed) // the actor brought fists to a ComponentRef<WeaponComponent> fight
        {
            // firearms cannot block
            return MeleeInteractionResponse.Unobstructed;
        }

        if (actorArmed)
            return MeleeInteractionResponse.StunVictim;

        return MeleeInteractionResponse.BlockVictim;
    }

    /// <summary>
    /// Returns a <see cref="MeleeInteractionResponse"/> based on the two given weapons
    /// </summary>
    public static MeleeInteractionResponse GetInteractionResponse(WeaponData actor, WeaponData victim)
    {
        if (actor.WeaponType != WeaponType.Melee || victim.WeaponType != WeaponType.Melee)
            return MeleeInteractionResponse.Invalid;

        if (victim.SpecialMelee)
            return MeleeInteractionResponse.BlockVictim;
        else if (actor.SpecialMelee)
            return MeleeInteractionResponse.StunVictim;

        switch (actor.MeleeDamageType)
        {
            case MeleeDamageType.Blade:
            case MeleeDamageType.Axe:
                {
                    switch (actor.MeleeSize)
                    {
                        case MeleeSize.Small:
                            return victim.MeleeDamageType == MeleeDamageType.Blunt && victim.MeleeSize == MeleeSize.Large ? MeleeInteractionResponse.StunVictim : MeleeInteractionResponse.BlockVictim;
                        default:
                            return
                                (victim.MeleeDamageType is MeleeDamageType.Blade or MeleeDamageType.Axe) && victim.MeleeSize == MeleeSize.Small ? MeleeInteractionResponse.StunVictim : MeleeInteractionResponse.BlockVictim;
                    }
                }
            case MeleeDamageType.Blunt:
                {
                    switch (actor.MeleeSize)
                    {
                        case MeleeSize.Large:
                            if (victim.MeleeSize is MeleeSize.Small && victim.MeleeDamageType is MeleeDamageType.Blade)
                                return MeleeInteractionResponse.StunVictim;
                            else if (victim.MeleeSize is MeleeSize.Medium)
                                return MeleeInteractionResponse.StunVictim;
                            else
                                return MeleeInteractionResponse.BlockVictim;
                        default:
                            if (victim.MeleeSize is MeleeSize.Small && victim.MeleeDamageType is MeleeDamageType.Blade)
                                return MeleeInteractionResponse.StunVictim;
                            else if (victim.MeleeSize is MeleeSize.Medium && victim.MeleeDamageType is MeleeDamageType.Blunt)
                                return MeleeInteractionResponse.StunVictim;
                            else
                                return MeleeInteractionResponse.BlockVictim;
                    }
                }
                break;
        }

        return MeleeInteractionResponse.BlockVictim; // fallback
    }
}