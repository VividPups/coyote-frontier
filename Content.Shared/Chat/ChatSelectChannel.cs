﻿namespace Content.Shared.Chat
{
    /// <summary>
    ///     Chat channels that the player can select in the chat box.
    /// </summary>
    /// <remarks>
    ///     Maps to <see cref="ChatChannel"/>, giving better names.
    /// </remarks>
    [Flags]
    public enum ChatSelectChannel : UInt32
    {
        None = 0,

        /// <summary>
        ///     Chat heard by players within earshot
        /// </summary>
        Local = ChatChannel.Local,

        /// <summary>
        ///     Chat heard by players right next to each other
        /// </summary>
        Whisper = ChatChannel.Whisper,

        /// <summary>
        ///     Radio messages
        /// </summary>
        Radio = ChatChannel.Radio,

        /// <summary>
        ///     Local out-of-character channel
        /// </summary>
        LOOC = ChatChannel.LOOC,

        /// <summary>
        ///     Out-of-character channel
        /// </summary>
        OOC = ChatChannel.OOC,

        /// <summary>
        ///     Emotes
        /// </summary>
        Emotes = ChatChannel.Emotes,

        /// <summary>
        ///     Subtle - Floofstation
        /// </summary>
        Subtle = ChatChannel.Subtle,

        /// <summary>
        ///     Deadchat
        /// </summary>
        Dead = ChatChannel.Dead,

        /// <summary>
        ///     Admin chat
        /// </summary>
        Admin = ChatChannel.AdminChat,

        /// <summary>
        ///     Admin chat
        /// </summary>
        Admin2 = 50,

        /// <summary>
        ///     Nyano - Summary:. Telepathic channel for all psionic entities.
        /// </summary>
        Telepathic = ChatChannel.Telepathic,

        Console = ChatChannel.Unspecified
    }
}
