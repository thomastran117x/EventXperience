namespace backend.main.features.auth.token
{
    /// <summary>
    /// Serialized into Redis by ordinal (Newtonsoft's default enum handling), so values may only
    /// ever be appended — reordering would make every in-flight verification decode as the wrong
    /// purpose across a deploy.
    /// </summary>
    public enum VerificationPurpose
    {
        SignUp,
        ResetPassword,
        ChangeEmail
    }
}
