namespace backend.main.shared.providers.messages
{
    /// <summary>
    /// Serialized onto the notification topic by ordinal, so values may only ever be
    /// appended and the email worker must be deployed no later than the producer.
    /// </summary>
    public enum EmailMessageType
    {
        VerifyEmail,
        ResetPassword,
        AccountConfirmation,
        NewDevice,
        MfaCode,
        EventInvite,
        Welcome,
        PasswordChanged,
        InvitationAccepted,
        InvitationDeclined,
        EventReminder,
        ClubStaffInvite,
        ClubMemberInvite,
        WaitlistJoined,
        WaitlistPromoted,
        UsernameReminder,
        ProviderSignInReminder,
        EmailChangeRequested,
        EmailChangeVerify,
        EmailChanged
    }
}
