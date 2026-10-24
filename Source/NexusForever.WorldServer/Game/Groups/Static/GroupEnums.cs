using System;

namespace NexusForever.WorldServer.Game.Groups.Static
{
    // Values and wire sizes follow the WildStar client, taken from the NexusForever group implementation.

    public enum GroupInviteResult
    {
        Sent                    = 0,
        Declined                = 1,
        Accepted                = 2,
        NoPermissions           = 3,
        PlayerNotFound          = 5,
        Grouped                 = 6,
        Pending                 = 7,
        ExpiredInviter          = 8,
        IsInvited               = 9,
        ExpiredInvitee          = 10,
        InvitedYou              = 11,
        RealmNotFound           = 12,
        Full                    = 13,
        RoleFull                = 14,
        NotInvitingSelf         = 15,
        ServerControlled        = 16,
        GroupNotFound           = 17,
        NotAcceptingRequests    = 18,
        Busy                    = 19,
        SentToLeader            = 20,
        LeaderOffline           = 21,
        WrongFaction            = 22,
        PrivilegeRestricted     = 23,
        PvpFlagRestriction      = 24
    }

    public enum RemoveReason
    {
        Kicked                    = 1,
        Disconnected              = 2,
        Left                      = 3,
        Disband                   = 4,
        RemovedByServer           = 5,
        VoteKicked                = 6,
        RemovedByServerForPvpFlag = 9
    }

    [Flags]
    public enum GroupFlags : uint
    {
        None            = 0,
        OpenWorld       = 1 << 0,
        Raid            = 1 << 1,
        JoinRequestOpen = 1 << 4,
        JoinRequestClosed = 1 << 5,
        ReferralsOpen   = 1 << 6,
        ReferralsClosed = 1 << 7
    }

    [Flags]
    public enum GroupMemberInfoFlags : uint
    {
        None                = 0,
        CanInvite           = 1 << 1,
        CanKick             = 1 << 2,
        Disconnected        = 1 << 3,
        Pending             = 1 << 4,
        Tank                = 1 << 5,
        Healer              = 1 << 6,
        DPS                 = 1 << 7,
        MainTank            = 1 << 8,
        MainAssist          = 1 << 9,
        RaidAssistant       = 1 << 10,
        Ready               = 1 << 11,
        RoleLocked          = 1 << 12,
        CanMark             = 1 << 13,
        HasSetReady         = 1 << 14,
        GroupMemberFlags    = CanMark,
        GroupAdminFlags     = CanInvite | CanKick | CanMark
    }

    public enum GroupActionResult
    {
        LeaveSuccess           = 1,
        LeaveFailed            = 2,
        DisbandSuccess         = 3,
        DisbandFailed          = 4,
        KickSuccess            = 5,
        KickFailed             = 6,
        PromoteSuccess         = 7,
        PromoteFailed          = 8,
        FlagsSuccess           = 9,
        FlagsFailed            = 10,
        MemberFlagsSuccess     = 11,
        MemberFlagsFailed      = 12,
        NotInGroup             = 13,
        ChangeSettingsSuccess  = 14,
        ChangeSettingsFailed   = 15,
        InvalidGroup           = 18
    }

    public enum LootRule : byte
    {
        FreeForAll      = 0,
        RoundRobin      = 1,
        NeedBeforeGreed = 2,
        Master          = 3
    }

    public enum LootThreshold : byte
    {
        Inferior  = 1,
        Average   = 2,
        Good      = 3,
        Excellent = 4,
        Superb    = 5,
        Legendary = 6,
        Artifact  = 7
    }

    public enum HarvestLootRule : byte
    {
        RoundRobin  = 0,
        FirstTagger = 1
    }
}
