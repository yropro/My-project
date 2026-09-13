/// <summary>
/// Fixed physical transport bank for transient Orrery presentation state.
///
/// This layer deliberately owns only transport identity/capacity. Spell codecs
/// continue to own payload layout, history/refresh semantics, visual lifetime,
/// and all gameplay behavior. Records are activated here incrementally as the
/// legacy spell-specific CoreNetwork registrations migrate.
/// </summary>
public static class OrreryPresentationNetwork
{
    public const int RecordCount = 6;

    public const byte Record0SlotId = 7;
    public const byte Record1SlotId = 8;
    public const byte Record2SlotId = 9;
    public const byte Record3SlotId = 10;
    public const byte Record4SlotId = 11;
    public const byte Record5SlotId = 12;

    public static byte GetSlotId(int recordIndex)
    {
        if (recordIndex < 0 || recordIndex >= RecordCount)
            return 0;
        return (byte)(Record0SlotId + recordIndex);
    }
}
