namespace AIVoiceActing.Domain.Chat;

/// <summary>
/// Chat channels Dalamud's XivChatType enum does not cover yet (verbatim port of
/// TextToTalk's AdditionalChatType — the numeric values are game wire values, shared with
/// the XivChatType range, so channel presets can reference both through one int).
/// </summary>
public enum AdditionalChatType
{
    ActionUsedOnYou = 2091,
    FailedActionUsedOnYou = 2218,
    ActionReadiedByYou = 2219,
    BeneficialEffectOnYou = 2222,
    BeneficialEffectOnYouEnded = 2224,
    DamageDealtByYou = 2729,
    DetrimentalEffectsInflictedByYou = 2735,
    EnemyDefeatedByYou = 2874,
    ActionUsedByOtherPlayer = 8235,
    BeneficialEffectOnOtherPlayer = 8750,
    DetrimentalEffectOnOtherPlayer = 8751,
    BeneficialEffectOnOtherPlayerEnded = 8752,
    FreeCompanyMemberLoginNotifications = 8774,
    ActionReadiedByEngagedEnemy = 10283,
    DamageYouAreDealt = 10409,
    FailedAttacksOnYou = 10410,
    DetrimentalEffectOnEnemyEnded = 10929,
}
