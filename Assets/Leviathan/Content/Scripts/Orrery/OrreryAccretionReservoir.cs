using System;

/// <summary>
/// One recipient-owned, allocation-free reservoir. A ticket is an earmark, not
/// absorption: only confirmed capture settles it. Unknown remote outcomes are
/// never refunded by a timer. The transport owns retry/abort decisions.
/// </summary>
public sealed class OrreryAccretionReservoir
{
    public const int TicketLimit = 64;
    private struct Ticket { public ulong Id; public float Amount; }
    private readonly Ticket[] tickets = new Ticket[TicketLimit];
    private ulong nextTicket;
    private float reserved;
    public uint Generation { get; private set; }
    public float Maximum { get; private set; }
    public float Remaining { get; private set; }
    public float ExpiresAt { get; private set; }
    public float Available { get { return Math.Max(0f, Remaining - reserved); } }
    public float Reserved { get { return reserved; } }
    public bool Valid { get { return Generation != 0u; } }

    public void Begin(uint generation, float capacity, float expiresAt)
    {
        if (generation == 0u || !Positive(capacity) || !Finite(expiresAt))
            throw new ArgumentOutOfRangeException("Invalid Accretion reservoir.");
        Invalidate();
        Generation = generation;
        Maximum = Remaining = capacity;
        ExpiresAt = expiresAt;
    }

    public bool CanAbsorb(float now)
    {
        return Valid && Finite(now) && now < ExpiresAt && Available > 0f;
    }

    /// <summary>True blocks the ENTIRE application. spent only controls cost/heal.
    /// Mutates the pool before the caller invokes any native callback.</summary>
    public bool TryAbsorb(float value, float now, out float spent)
    {
        spent = 0f;
        if (!Positive(value) || !CanAbsorb(now)) return false;
        spent = Math.Min(value, Available);
        Remaining -= spent;
        if (Remaining < reserved) Remaining = reserved; // float roundoff only
        return true;
    }

    public bool TryReserve(uint generation, float value, float now, out ulong ticket)
    {
        ticket = 0ul;
        if (generation != Generation || !Positive(value) || !CanAbsorb(now))
            return false;
        int index = -1;
        for (int i = 0; i < tickets.Length; i++)
            if (tickets[i].Id == 0ul) { index = i; break; }
        if (index < 0) return false;
        do { unchecked { nextTicket++; } } while (nextTicket == 0ul);
        ticket = nextTicket;
        float amount = Math.Min(value, Available);
        tickets[index] = new Ticket { Id = ticket, Amount = amount };
        reserved += amount;
        return true;
    }

    /// <summary>A successful result settles a previously authorized application.
    /// Recast/ship replacement invalidate its generation; an elapsed authored
    /// duration alone does not turn an already-authorized capture into a refund.</summary>
    public bool Settle(uint generation, ulong ticket, bool captured, out float spent)
    {
        spent = 0f;
        if (!Valid || generation != Generation || ticket == 0ul) return false;
        for (int i = 0; i < tickets.Length; i++)
        {
            if (tickets[i].Id != ticket) continue;
            float amount = tickets[i].Amount;
            tickets[i] = default(Ticket);
            reserved = Math.Max(0f, reserved - amount);
            if (captured)
            {
                spent = Math.Min(amount, Remaining);
                Remaining = Math.Max(0f, Remaining - spent);
            }
            return true;
        }
        return false;
    }

    public void Invalidate()
    {
        Generation = 0u;
        Remaining = reserved = 0f;
        Array.Clear(tickets, 0, tickets.Length);
        // A process-lifetime ticket counter must not alias after replacement.
    }

    public static bool Finite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
    public static bool Positive(float value) { return Finite(value) && value > 0f; }
}
