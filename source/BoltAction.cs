using Cairo;
using CombatOverhaul;
using CombatOverhaul.Animations;
using CombatOverhaul.Implementations;
using CombatOverhaul.Inputs;
using CombatOverhaul.RangedSystems;
using CombatOverhaul.RangedSystems.Aiming;
using System;
using OpenTK.Mathematics;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using System.Diagnostics;
using Vintagestory.Client.NoObf;

namespace Glockmaker.source;

public enum BoltActionState //a few of these are probably unused
{
    Unloaded, //the gun is unloaded
    Flip, //the gun is flipping over to be loaded
    LoadBullet, //the gun is being loaded
    ReadyToLoad, //the gun is ready to be loaded
    Load,
    PartiallyLoaded, //the gun is not fully loaded, but has bullets left in the magazine
    FullyLoaded, //the gun is fully loaded
    Unflip, //the gun is being flipped back over to be used
    Chambering, //the gun is chambering a round and the bolt is moving forwards
    Unchambered,
    Chambered, //the gun has been chambered with a bullet and the bolt is ready
    Ejecting, //the bolt is being pulled back to eject a round
    Ejected, //the round has been ejected and the bolt is pulled back
    Ready, //the gun is aimed and ready to fire
    Shoot, //fire da gun
    Shot //the gun has been shot
}

public class BoltActionStats : WeaponStats
{
    public string FlipAnimation { get; set; } = ""; //the gun is flipping over to be loaded
    public string LoadBulletAnimation { get; set; } = ""; //the gun is being loaded
    public string UnflipAnimation { get; set; } = ""; //the gun is being flipped back over to be used
    public string AimAnimation { get; set; } = ""; //the gun is aimed and ready to fire
    public string ShootAnimation { get; set; } = ""; //fire da gun
    public string EaseAnimation { get; set; } = ""; //idle
    public string RackAnimation { get; set; } = ""; //the bolt is being pulled back to eject a round
    public string ChamberAnimation { get; set; } = ""; //the gun is chambering a round and the bolt is moving forwards
    public string[] LoadedAnimation { get; set; } = Array.Empty<string>(); //the gun is fully loaded

    public float LoadSpeedPenalty { get; set; } = -0.6f;
    public float BoltSpeedPenalty { get; set; } = -0.1f;

    public AimingStatsJson Aiming { get; set; } = new();
    public float[] DispersionMOA { get; set; } = new float[] { 0, 0 };
    public float BulletDamageModifier { get; set; } = 1;
    public float BulletDamageStrength { get; set; } = 1;
    public float BulletVelocity { get; set; } = 1;
    public int Multishot { get; set; } = 1;
    public int BulletsPerLoad { get; set;} = 1;
    public string BulletWildCard { get; set; } = "@.*(cased-bullet-paper|cased-bullet-brass)";
    public float Zeroing { get; set; } = 1.5f;

    public int MagazineSize { get; set; } = 5;

    public float ReloadAnimationSpeed { get; set; } = 1;

    public bool chambered { get; set; } = false;

    public string fullAmmoEject { get; set; } = "";
    public string spentAmmoEject { get; set; } = "";
}

public enum BoltActionLoadingStage
{
    Unloaded,
    Loading,
    Chambered,
    Unchambered
}

public class BoltActionClient : RangeWeaponClient
{
    protected AimingAnimationController? AimingAnimationController;
    protected readonly AnimatableAttachable Attachable;
    protected readonly ClientAimingSystem AimingSystem;
    protected readonly BoltActionStats Stats;
    protected readonly AimingStats AimingStats;
    protected readonly ItemInventoryBuffer Inventory = new();
    protected readonly ModelTransform BulletTransform;
    protected readonly AmmoSelector AmmoSelector;
    protected const string InventoryId = "magazine";
    protected const string LoadingStageAttribute = "CombatOverhaul:loading-stage";
    protected ItemSlot? BulletSlot;
    protected static ItemSlot? GunSlot;

    protected const string PlayerStatsMainHandCategory = "CombatOverhaul:held-item-mainhand";
    protected const string PlayerStatsOffHandCategory = "CombatOverhaul:held-item-offhand";

    public BoltActionClient(ICoreClientAPI api, Item item, AmmoSelector ammoSelector) : base(api, item)
    {
        Attachable = item.GetCollectibleBehavior<AnimatableAttachable>(withInheritance: true) ?? throw new Exception("Gun should have AnimateableAttachable behavior");
        BulletTransform = new(item.Attributes["BulletTransform"].AsObject<ModelTransformNoDefaults>(), ModelTransform.BlockDefaultTp());
        AimingSystem = api.ModLoader.GetModSystem<CombatOverhaulSystem>().AimingSystem ?? throw new Exception();
        Stats = item.Attributes.AsObject<BoltActionStats>();
        AimingStats = Stats.Aiming.ToStats();
        AmmoSelector = ammoSelector;
    }

    public override void OnSelected(ItemSlot slot, EntityPlayer player, bool mainHand, ref int state)
    {
        Attachable.ClearAttachments(player.EntityId);
        AttachmentSystem.SendClearPacket(player.EntityId);

        Inventory.Read(slot, InventoryId);

        BoltActionLoadingStage stage = GetLoadingStage<BoltActionLoadingStage>(slot);
        state = stage switch
        {
            BoltActionLoadingStage.Unloaded => (int)BoltActionState.Unloaded,
            BoltActionLoadingStage.Loading => (int)BoltActionState.Unchambered,
            BoltActionLoadingStage.Chambered => (int)BoltActionState.Chambered,
            BoltActionLoadingStage.Unchambered => (int)BoltActionState.Unchambered,
            _ => (int)BoltActionState.Unloaded
        };

        switch (stage)
        {
            case BoltActionLoadingStage.Unloaded:
                AnimationBehavior?.Play(mainHand, GetLoadingAnimation(slot, Stats.LoadedAnimation), weight: 0.001f);
                TpAnimationBehavior?.Play(mainHand, GetLoadingAnimation(slot, Stats.LoadedAnimation), weight: 0.001f);
                break;
            case BoltActionLoadingStage.Loading:
                AnimationBehavior?.Play(mainHand, GetLoadingAnimation(slot, Stats.LoadedAnimation), weight: 0.001f);
                TpAnimationBehavior?.Play(mainHand, GetLoadingAnimation(slot, Stats.LoadedAnimation), weight: 0.001f);
                break;
        }
    }

    public override void OnDeselected(EntityPlayer player, bool mainHand, ref int state)
    {
        Attachable.ClearAttachments(player.EntityId);
        AimingAnimationController?.Stop(mainHand);
        AimingSystem.AimingState = WeaponAimingState.None;
        AimingSystem.StopAiming();
        AnimationBehavior?.StopAllVanillaAnimations(mainHand);
        PlayerBehavior?.SetStat("walkspeed", mainHand ? PlayerStatsMainHandCategory : PlayerStatsOffHandCategory);
    }

    public override void OnRegistered(ActionsManagerPlayerBehavior behavior, ICoreClientAPI api)
    {
        base.OnRegistered(behavior, api);
        AimingAnimationController = new(AimingSystem, AnimationBehavior, AimingStats);
    }

    [ActionEventHandler(EnumEntityAction.CtrlKey, ActionState.Active)]
    protected virtual bool Flipped(ItemSlot slot, EntityPlayer player, ref int state, ActionEventData eventData, bool mainHand, AttackDirection direction)
    {
        if (!CheckState(state, BoltActionState.Unloaded, BoltActionState.Chambered)) return false;
        if (!CheckForOtherHandEmpty(mainHand, player)) return false;

        Inventory.Read(slot, InventoryId);
        if (Inventory.Items.Count >= Stats.MagazineSize)
        {
            Inventory.Clear();
            return false;
        }

        if (!eventData.AltPressed)
        {
            Inventory.Clear();
            state = (int)BoltActionState.Flip;

            ItemStackRangedStats stackStats = ItemStackRangedStats.FromItemStack(slot.Itemstack);

            AnimationBehavior?.Play(mainHand, Stats.FlipAnimation, callback: FlippedCallback, animationSpeed: GetAnimationSpeed(player, Stats.ProficiencyStat) * stackStats.ReloadSpeed * Stats.ReloadAnimationSpeed);
            TpAnimationBehavior?.Play(mainHand, Stats.FlipAnimation, animationSpeed: GetAnimationSpeed(player, Stats.ProficiencyStat) * stackStats.ReloadSpeed * Stats.ReloadAnimationSpeed);

            return true;
        } else {
            return false;
        }
    }
    protected virtual bool FlippedCallback()
    {
        PlayerBehavior?.SetState((int)BoltActionState.ReadyToLoad, mainHand: true);
        return true;
    }

    [ActionEventHandler(EnumEntityAction.RightMouseDown, ActionState.Active)]
    protected virtual bool Load(ItemSlot slot, EntityPlayer player, ref int state, ActionEventData eventData, bool mainHand, AttackDirection direction)
    {
        if (!CheckState(state, BoltActionState.ReadyToLoad)) return false;
        if (!mainHand || !CheckForOtherHandEmpty(mainHand, player)) return false;

        Inventory.Read(slot, InventoryId);

        Debug.WriteLine("Reload: Bullet Count: " + Inventory.Items.Count);

        if (Inventory.Items.Count >= Stats.MagazineSize)
        {
            Inventory.Clear();

            Unflip(slot, player, ref state, eventData, mainHand, direction);

            return false;
        }
        Inventory.Clear();

        ItemSlot? ammoSlot = null;
        player.WalkInventory(slot =>
        {
            if (slot?.Itemstack?.Item == null) return true;
            Debug.WriteLine("Reload: Gets past null");
            if (slot.Itemstack.Item.HasBehavior<ProjectileBehavior>() && WildcardUtil.Match(AmmoSelector.SelectedAmmo, slot.Itemstack.Item.Code.ToString()))
            {
                ammoSlot = slot;
                return false;
            }

            return true;
        });

        if (ammoSlot == null)
        {
            Api.TriggerIngameError(this, "needMoreBoolets", Lang.Get("glockmaker:need-more-boolets"));
            return false;
        }

        Attachable.SetAttachment(player.EntityId, "bullet", ammoSlot.Itemstack, BulletTransform);
        AttachmentSystem.SendAttachPacket(player.EntityId, "bullet", ammoSlot.Itemstack, BulletTransform);

        ItemStackRangedStats stackStats = ItemStackRangedStats.FromItemStack(slot.Itemstack);
        
        state = (int)BoltActionState.LoadBullet;
        AnimationBehavior?.Play(mainHand, Stats.LoadBulletAnimation, animationSpeed: GetAnimationSpeed(player, Stats.ProficiencyStat) * stackStats.ReloadSpeed * Stats.ReloadAnimationSpeed, callback: () => LoadCallback(slot, ammoSlot, player, mainHand, 2));
        TpAnimationBehavior?.Play(mainHand, Stats.LoadBulletAnimation, animationSpeed: GetAnimationSpeed(player, Stats.ProficiencyStat) * stackStats.ReloadSpeed * Stats.ReloadAnimationSpeed);
        

        return true;
    }
    protected virtual bool LoadCallback(ItemSlot slot, ItemSlot ammoSlot, EntityPlayer player, bool mainHand, int lastAmmoToLoad)
    {
        RangedWeaponSystem.Reload(slot, ammoSlot, 1, true, success =>  LoadServerCallback(success, lastAmmoToLoad, mainHand));
        Attachable.ClearAttachments(player.EntityId);
        AttachmentSystem.SendClearPacket(player.EntityId);
        return true;
    }
    protected virtual void LoadServerCallback(bool success, int lastAmmoToLoad, bool mainHand)
    {
        int state = PlayerBehavior?.GetState(mainHand: true) ?? 0;

        if (state == (int)BoltActionState.LoadBullet)
        {
            PlayerBehavior?.SetState((int)BoltActionState.ReadyToLoad, mainHand: true);
            switch (lastAmmoToLoad)
            {
                case 0: //unloaded
                    break;
                case 1: //partially loaded
                    break;
                case 2: //fully loaded
                    break;
            }
        }
    }

    [ActionEventHandler(EnumEntityAction.RightMouseDown, ActionState.Active)]
    protected virtual bool Aim(ItemSlot slot, EntityPlayer player, ref int state, ActionEventData eventData, bool mainHand, AttackDirection direction)
    {
        if (!CheckState(state, BoltActionState.Chambered)) return false;
        if (!mainHand) return false;
        if (eventData.AltPressed) return false;

        ItemStackRangedStats stackStats = ItemStackRangedStats.FromItemStack(slot.Itemstack);
        AimingStats aimingStats = AimingStats.Clone();
        aimingStats.AimDifficulty *= stackStats.AimingDifficulty;

        SetState(BoltActionState.Ready, mainHand);
        AnimationBehavior?.Play(mainHand, Stats.AimAnimation, callback: () => AimCallback());
        TpAnimationBehavior?.Play(mainHand, Stats.AimAnimation);
        AnimationBehavior?.StopAllVanillaAnimations(mainHand);

        if (TpAnimationBehavior == null) AnimationBehavior?.PlayVanillaAnimation(Stats.AimAnimation, mainHand);

        AimingSystem.StartAiming(aimingStats);
        AimingSystem.AimingState = WeaponAimingState.FullCharge;
        AimingAnimationController?.Play(mainHand);

        return true;
    }
    protected virtual bool AimCallback()
    {
        return true;
    }

    [ActionEventHandler(EnumEntityAction.RightMouseDown, ActionState.Released)]
    protected virtual bool Ease(ItemSlot slot, EntityPlayer player, ref int state, ActionEventData eventData, bool mainHand, AttackDirection direction)
    {
        PlayerBehavior?.SetStat("walkspeed", mainHand ? PlayerStatsMainHandCategory : PlayerStatsOffHandCategory);

        switch ((BoltActionState)state)
        {
            case BoltActionState.Ready:
                {
                    if (Stats.chambered)
                    {
                        SetState(BoltActionState.Chambered, mainHand);
                        Attachable.ClearAttachments(player.EntityId);
                    }
                    else
                    {
                        SetState(BoltActionState.Unchambered, mainHand);
                        Attachable.ClearAttachments(player.EntityId);
                    }
                }
                break;
            case BoltActionState.Shot:
                {
                    Inventory.Read(slot, InventoryId);
                    if (Inventory.Items.Count == 0)
                    {
                        SetState(BoltActionState.Unchambered, mainHand);
                    }
                    else
                    {
                        SetState(BoltActionState.Unchambered, mainHand);
                    }

                    Inventory.Clear();
                }
                break;
            case BoltActionState.Unloaded:
                {
                    Inventory.Read(slot, InventoryId);
                    if (Inventory.Items.Count == 0)
                    {
                        SetState(BoltActionState.Unloaded, mainHand);
                    } 
                    else if (Stats.chambered)
                    {
                        SetState(BoltActionState.Chambered, mainHand);
                    }
                    else
                    {
                        SetState(BoltActionState.Unchambered, mainHand);
                    }

                    Inventory.Clear();

                    break;
                }
        }

        AnimationBehavior?.Play(mainHand, Stats.EaseAnimation);
        TpAnimationBehavior?.Play(mainHand, Stats.EaseAnimation);
        AimingSystem.StopAiming();
        AimingAnimationController?.Stop(mainHand);
        AnimationBehavior?.StopAllVanillaAnimations(mainHand);

        Debug.WriteLine("Current Weapon State: " + GetState<BoltActionState>().ToString());

        return false;

    }

    protected static void SetLoadingStage<TStage>(ItemSlot slot, TStage stage)
        where TStage : struct, Enum
    {
        slot.Itemstack?.Attributes.SetInt(LoadingStageAttribute, (int)Enum.ToObject(typeof(TStage), stage));
        slot.MarkDirty();
    }

    [ActionEventHandler(EnumEntityAction.LeftMouseDown, ActionState.Pressed)]
    protected virtual bool Shoot(ItemSlot slot, EntityPlayer player, ref int state, ActionEventData eventData, bool mainHand, AttackDirection direction)
    {
        if (!CheckState(state, BoltActionState.Ready)) return false;
        if (eventData.AltPressed) return false;

        Inventory.Read(slot, InventoryId);
        if (Inventory.Items.Count == 0)
        {
            Inventory.Clear();
            return false;
        }
        Inventory.Clear();

        SetState(BoltActionState.Shot, mainHand);
        Stats.chambered = false;

        AnimationBehavior?.Stop(PlayerStatsMainHandCategory);
        AnimationBehavior?.Play(
            mainHand,
            Stats.ShootAnimation,
            callback: () => ShootCallback(slot, player, mainHand),
            callbackHandler: callback => ShootAnimationCallback(callback, slot, player, mainHand));
        TpAnimationBehavior?.Stop(PlayerStatsMainHandCategory);
        TpAnimationBehavior?.Play(
            mainHand,
            Stats.ShootAnimation);

        return true;
    }
    protected virtual bool ShootCallback(ItemSlot slot, EntityPlayer player, bool mainHand)
    {
        SetState(BoltActionState.Shot, mainHand);

        Inventory.Read(slot, InventoryId);
        if (Inventory.Items.Count >= 0)
        {
            SetLoadingStage(slot, BoltActionLoadingStage.Unchambered);
        } else
        {
            SetLoadingStage(slot, BoltActionLoadingStage.Unloaded);
        }
        Stats.chambered = false;
        return true;
    }
    protected virtual void ShootAnimationCallback(string callback, ItemSlot slot, EntityPlayer player, bool mainHand)
    {
        switch (callback)
        {
            case "shoot":
                Vintagestory.API.MathTools.Vec3d position = player.LocalEyePos + player.Pos.XYZ;
                Vector3 targetDirection = AimingSystem.TargetVec;

                targetDirection = ClientAimingSystem.Zeroing(targetDirection, Stats.Zeroing);

                Debug.WriteLine("Shoot Callback: Reaches Shoot Section.");

                RangedWeaponSystem.Shoot(slot, Stats.Multishot, new ((float)position.X, (float)position.Y, (float)position.Z), new (targetDirection.X, targetDirection.Y, targetDirection.Z), mainHand, ShootServerCallback);

                break;
        }
    }
    protected virtual void ShootServerCallback(bool success)
    {
        //why does this even exist?
    }

    [ActionEventHandler(EnumEntityAction.LeftMouseDown, ActionState.Pressed)]
    protected virtual bool Rack(ItemSlot slot, EntityPlayer player, ref int state, ActionEventData eventData, bool mainHand, AttackDirection direction)
    {
        Debug.WriteLine("Bolt Rack: Reads Input");

        if (eventData.AltPressed) return false;
        if (!CheckState(state, BoltActionState.Chambered, BoltActionState.Unchambered, BoltActionState.Ejected, BoltActionState.LoadBullet)) return false;

        Debug.WriteLine("Bolt Rack: Passes State Check.");

        ItemStackRangedStats stackStats = ItemStackRangedStats.FromItemStack(slot.Itemstack);

        Inventory.Read(slot, InventoryId);
        if (CheckState(state, BoltActionState.Ejected))
        {
            ItemStack ejectedItem = new ItemStack();

            if (Stats.chambered)
            {
                Inventory.Items.Remove(Inventory.Items[0]);
                Debug.WriteLine("Bolt Rack: Current Bullet Count: " + Inventory.Items.Count);

                slot.MarkDirty();
                Inventory.Write(slot);
                SetSlotData(slot);
                
            }
            
            if (Inventory.Items.Count > 0)
            {
                SetState(BoltActionState.Chambered, mainHand);
                SetLoadingStage(slot, BoltActionLoadingStage.Chambered);
                Stats.chambered = true;
            }
            else
            {
                SetState(BoltActionState.Unloaded, mainHand);
                SetLoadingStage(slot, BoltActionLoadingStage.Unloaded);
                Stats.chambered = false;
            }

            Inventory.Clear();

            AnimationBehavior?.Play(mainHand, Stats.ChamberAnimation, callback: () => RackCallback(slot), animationSpeed: GetAnimationSpeed(player, Stats.ProficiencyStat) * stackStats.ReloadSpeed * Stats.ReloadAnimationSpeed);
            TpAnimationBehavior?.Play(mainHand, Stats.ChamberAnimation, animationSpeed: GetAnimationSpeed(player, Stats.ProficiencyStat) * stackStats.ReloadSpeed * Stats.ReloadAnimationSpeed);
            return true;
        }
        else
        {
            SetState(BoltActionState.Ejected, mainHand);
            SetLoadingStage(slot, BoltActionLoadingStage.Unchambered);

            BoltActionServer.instance.Rack(Stats);

            Inventory.Clear();

            AnimationBehavior?.Play(mainHand, Stats.RackAnimation, callback: () => RackCallback(slot), animationSpeed: GetAnimationSpeed(player, Stats.ProficiencyStat) * stackStats.ReloadSpeed * Stats.ReloadAnimationSpeed);
            TpAnimationBehavior?.Play(mainHand, Stats.RackAnimation, animationSpeed: GetAnimationSpeed(player, Stats.ProficiencyStat) * stackStats.ReloadSpeed * Stats.ReloadAnimationSpeed);
            return true;
        }
    }
    protected virtual bool RackCallback(ItemSlot slot)
    {
        return true;
    }

    [ActionEventHandler(EnumEntityAction.RightMouseDown, ActionState.Released)]
    protected virtual bool Unflip(ItemSlot slot, EntityPlayer player, ref int state, ActionEventData eventData, bool mainHand, AttackDirection direction)
    {
        if (!CheckState(state, BoltActionState.ReadyToLoad, BoltActionState.Load, BoltActionState.Flip)) return false;
        if (eventData.AltPressed) return false;

        ItemStackRangedStats stackStats = ItemStackRangedStats.FromItemStack(slot.Itemstack);

        AnimationBehavior?.Play(mainHand, Stats.UnflipAnimation, callback: () => UnflipCallback(slot, Stats.chambered), animationSpeed: GetAnimationSpeed(player, Stats.ProficiencyStat) * stackStats.ReloadSpeed * Stats.ReloadAnimationSpeed);
        TpAnimationBehavior?.Play(mainHand, Stats.UnflipAnimation, animationSpeed: GetAnimationSpeed(player, Stats.ProficiencyStat) * stackStats.ReloadSpeed * Stats.ReloadAnimationSpeed);

        return true;
    }
    protected virtual bool UnflipCallback(ItemSlot slot, bool chambered)
    {
        if (chambered)
        {
            PlayerBehavior?.SetState((int)BoltActionState.Chambered, mainHand: true);

            Inventory.Clear();

            return true;
        }
        else
        {
            PlayerBehavior?.SetState((int)BoltActionState.Unchambered, mainHand: true);

            Inventory.Clear();

            return true;
        }
    }
    //
    protected void PutIntoMagazine(ItemSlot slot, ItemSlot ammoSlot)
    {
        Inventory.Read(slot, InventoryId);
        ItemStack ammo = ammoSlot.TakeOut(Stats.BulletsPerLoad);
        Inventory.Items.Add(ammo);
        Inventory.Write(slot);
        Inventory.Clear();
    }
    protected int LeftInMagazine(ItemSlot slot)
    {
        Inventory.Read(slot, InventoryId);
        int size = Inventory.Items.Count;
        Inventory.Clear();

        return size;
    }
    protected int EmptySpaceLeft(ItemSlot slot)
    {
        Inventory.Read(slot, InventoryId);
        int size = Stats.MagazineSize - Inventory.Items.Count;
        Inventory.Clear();

        return size;
    }

    protected string GetLoadingAnimation(ItemSlot slot, string[] animations)
    {
        Inventory.Read(slot, InventoryId);

        int ammoCount = Inventory.Items.Count;
        int ammoPerLoad = 1;
        int animationsCount = animations.Length;
        int index = (ammoCount / ammoPerLoad) % animationsCount;

        Inventory.Clear();

        return animations[index];
    }
    protected static TStage GetLoadingStage<TStage>(ItemSlot slot)
        where TStage : struct, Enum
    {
        int stage = slot.Itemstack?.Attributes.GetAsInt(LoadingStageAttribute, 0) ?? 0;
        return (TStage)Enum.ToObject(typeof(TStage), stage);  
    }

    protected static byte[] SerializeLoadingStage<TStage>(TStage stage)
        where TStage: struct, Enum
    {
        int stageInt = (int)Enum.ToObject(typeof(TStage), stage);
        return BitConverter.GetBytes(stageInt); 
    }

    protected bool CanUseOffhand(EntityPlayer player)
    {
        if (player.RightHandItemSlot?.Itemstack?.Item is not BoltActionItem weapon) return true;

        BoltActionState state = GetState<BoltActionState>(mainHand: true);
        return state switch
        {
            BoltActionState.Unloaded => true,
            BoltActionState.Flip => true,
            BoltActionState.ReadyToLoad => true,
            _ => false
        };
    }

    private void SetSlotData(ItemSlot slot)
    {
        GunSlot = slot;
    }

}

public class BoltActionServer : RangeWeaponServer
{
    protected BoltActionStats Stats;
    protected readonly ItemInventoryBuffer Inventory = new();
    protected const string InventoryId = "magazine";
    protected const string LoadingStageAttribute = "CombatOverhaul:loading-stage";
    protected readonly BoltActionLoadingStage LastStage = BoltActionLoadingStage.Loading;
    protected static IServerPlayer globalPlayer { get; set; } = null;
    protected static ItemSlot globalSlot { get; set; }

    public BoltActionServer(ICoreServerAPI api, Item item) : base(api, item)
    {
        Stats = item.Attributes.AsObject<BoltActionStats>();
    }

    public static BoltActionServer instance;

    public void Rack(BoltActionStats stats)
    {
        IServerPlayer player = GetPlayer();
        ItemSlot slot = GetSlot();

        this.Inventory.Read(slot, InventoryId);

        ItemStack ejectedItem = new ItemStack();

        Debug.WriteLine("Bolt Rack: Chambered Status: " + Stats.chambered);

        if (stats.chambered)
        {
            JsonItemStack temp = new()
            {
                Type = EnumItemClass.Item,
                Code = "glockmaker:cased-bullet-lead"
            };
            Debug.WriteLine("Bolt Rack: Creates Itemstack (Bullet)");

            temp.Resolve(Api.World, "");
            ejectedItem = temp.ResolvedItemstack;

            Inventory.Items.Remove(Inventory.Items[0]);
            Debug.WriteLine("Bolt Rack: Current Bullet Count: " + Inventory.Items.Count);

            slot.MarkDirty();
            Inventory.Write(slot);

            SetSlot(slot);
        }
        else if (Inventory.Items.Count != Stats.MagazineSize)
        {
            JsonItemStack temp = new()
            {
                Type = EnumItemClass.Item,
                Code = "glockmaker:bullet-casing-brass-lead"
            };
            Debug.WriteLine("Bolt Rack: Creates Itemstack (Casing)");

            temp.Resolve(Api.World, "");
            ejectedItem = temp.ResolvedItemstack;
        }

        Api.World.SpawnItemEntity(ejectedItem, player.Entity.Pos.AsBlockPos);

        Inventory.Clear();
    }

    private void SetPlayer(IServerPlayer player)
    {
        globalPlayer = player;
    }

    private void SetSlot(ItemSlot slot)
    {
        globalSlot = slot;
    }

    private static IServerPlayer GetPlayer()
    {
        return globalPlayer;
    }
    
    private static ItemSlot GetSlot()
    {
        return globalSlot;
    }

    public override bool Reload(IServerPlayer player, ItemSlot slot, ItemSlot? ammoSlot, ReloadPacket packet)
    {
        BoltActionLoadingStage currentStage = BoltActionLoadingStage.Unloaded;

        instance = this;

        SetPlayer(player);
        SetSlot(slot);

        if (ammoSlot != null && slot != null)
        {
            Inventory.Read(slot, InventoryId);

            Debug.WriteLine("Server Reload: Current Bullet Count: " + Inventory.Items.Count);

            if (Inventory.Items.Count >= Stats.MagazineSize) return false;
            Debug.WriteLine("Server Reload: Reaches Load Checks");

            if (
                ammoSlot.Itemstack?.Item?.Code != null &&
                ammoSlot.Itemstack.Item.HasBehavior<ProjectileBehavior>() &&
                WildcardUtil.Match(Stats.BulletWildCard, ammoSlot.Itemstack.Item.Code.ToString()) &&
                ammoSlot.Itemstack.StackSize >= Stats.BulletsPerLoad)
            {
                Debug.WriteLine("Server Reload: Reaches Loading");

                for (int count = 0; count < Stats.BulletsPerLoad; count++)
                {
                    ItemStack ammo = ammoSlot.TakeOut(1);
                    Inventory.Items.Add(ammo);

                    Debug.WriteLine("Server Reload: Loaded Round");
                }

                ammoSlot.MarkDirty();
                Inventory.Write(slot);
                Inventory.Clear();
            }
            else
            {
                return false;
            }
        }

        SetLoadingStage(slot, currentStage);

        return true;
    }

    

    public override bool Shoot(IServerPlayer player, ItemSlot slot, ShotPacket packet, Entity shooter)
    {
        Debug.WriteLine("Server Shoot: packet.Amount = " + packet.Amount);

        BoltActionLoadingStage finishedStage = GetLoadingStage<BoltActionLoadingStage>(slot);
        //if (finishedStage != LastStage) return false;

        Debug.WriteLine("Server Shoot: packet.Amount = " + packet.Amount);

        Inventory.Read(slot, InventoryId);

        if (Inventory.Items.Count == 0) return false;

        int count = 0;
        int additionalDurabilityCost = 0;

        while (Inventory.Items.Count > 0 && count < packet.Amount)
        {
            ItemStack ammo = Inventory.Items[0];
            ammo.ResolveBlockOrItem(Api.World);
            Inventory.Items.RemoveAt(0);

            ProjectileStats? stats = ammo.Item?.GetCollectibleBehavior<ProjectileBehavior>(true)?.GetStats(ammo);

            if (stats == null) continue;

            ItemStackRangedStats stackStats = ItemStackRangedStats.FromItemStack(slot.Itemstack);

            additionalDurabilityCost = Math.Max(additionalDurabilityCost, stats.AdditionalDurabilityCost);

            ProjectileSpawnStats spawnStats = new()
            {
                ProducerEntityId = player.Entity.EntityId,
                DamageMultiplier = Stats.BulletDamageModifier * stackStats.DamageMultiplier,
                DamageStrength = Stats.BulletDamageStrength + stackStats.DamageTierBonus,
                Position = new Vector3d(packet.Position[0], packet.Position[1], packet.Position[2]),
                Velocity = GetDirectionWithDispersion(packet.Velocity, new float[2] { Stats.DispersionMOA[0] * stackStats.DispersionMultiplier, Stats.DispersionMOA[1] * stackStats.DispersionMultiplier }) * Stats.BulletVelocity * stackStats.ProjectileSpeed,
            };

            ProjectileSystem.Spawn(packet.ProjectileId[count], stats, spawnStats, ammo, slot.Itemstack, shooter);
            Debug.WriteLine("Server Shoot: Shot Bullet");

            count++;
        }

        slot.Itemstack.Item.DamageItem(player.Entity.World, player.Entity, slot, 1 + additionalDurabilityCost);
        slot.MarkDirty();

        Inventory.Write(slot);
        int ammoLeft = Inventory.Items.Count;
        Inventory.Clear();

        if (ammoLeft == 0) SetLoadingStage(slot, BoltActionLoadingStage.Unchambered);

        return true;
    }

    protected static TStage GetLoadingStage<TStage>(ReloadPacket packet) where TStage : struct, Enum
    {
        int stage = BitConverter.ToInt32(packet.Data, 0);
        return (TStage)Enum.ToObject(typeof(TStage), stage);
    }
    protected static TStage GetLoadingStage<TStage>(ItemSlot slot) where TStage : struct, Enum
    {
        int stage = slot.Itemstack?.Attributes.GetAsInt(LoadingStageAttribute, 0) ?? 0;
        return (TStage)Enum.ToObject(typeof(TStage), stage);
    }
    protected static void SetLoadingStage<TStage>(ItemSlot slot, TStage stage) where TStage : struct, Enum
    {
        slot.Itemstack?.Attributes.SetInt(LoadingStageAttribute, (int)Enum.ToObject(typeof(TStage), stage));
        slot.MarkDirty();
    }



}

public class BoltActionItem : Item, IHasWeaponLogic, IHasRangedWeaponLogic
{
    public BoltActionClient? ClientLogic { get; private set; }
    public BoltActionServer? ServerLogic { get; private set; }

    public AnimationRequestByCode IdleAnimation { get; private set; }
    public AnimationRequestByCode ReadyAnimation { get; private set; }

    public BoltActionStats? Stats { get; private set; }
    private AmmoSelector? _ammoSelector;

    IClientWeaponLogic? IHasWeaponLogic.ClientLogic => ClientLogic;
    IServerRangedWeaponLogic? IHasRangedWeaponLogic.ServerWeaponLogic => ServerLogic;

    public AnimationRequestByCode? GetIdleAnimation(EntityPlayer player, ItemSlot slot, bool mainHand) => IdleAnimation;
    public AnimationRequestByCode? GetReadyAnimation(EntityPlayer player, ItemSlot slot, bool mainHand) => ReadyAnimation;

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        if (Stats != null && Stats.ProficiencyStat != "")
        {
            string description = Lang.Get("combatoverhaul:iteminfo-proficiency", Lang.Get($"combatoverhaul:proficiency-{Stats.ProficiencyStat}"));
            dsc.AppendLine(description);
        }
        
        if (Stats != null)
        {
            string description = Lang.Get("combatoverhaul:iteminfo-range-weapon-damage", Stats.BulletDamageModifier, Stats.BulletDamageStrength);
            dsc.AppendLine(description);
        }

        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
    }

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);

        Stats = Attributes.AsObject<BoltActionStats>();

        if (api is ICoreClientAPI clientAPI)
        {
            _ammoSelector = new(clientAPI, Stats.BulletWildCard);

            ClientLogic = new(clientAPI, this, _ammoSelector);

            Stats = Attributes.AsObject<BoltActionStats>();
            IdleAnimation = new(Stats.IdleAnimation, 1, 1, "main", TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(0.2), false);
            ReadyAnimation = new(Stats.ReadyAnimation, 1, 1, "main", TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(0.2), false);
        }

        if (api is ICoreServerAPI serverAPI)
        {
            ServerLogic = new(serverAPI, this);
        }
    }
}


