using Clinic.Api.Modules.Appointments;
using Xunit;

namespace Clinic.Api.Tests;

public class AppointmentRulesTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("PT-1", 0)]
    [InlineData("PT-1", -3)]
    public void EmptyInput_IsRejected(string? reference, long slots) =>
        Assert.NotNull(AppointmentRules.Validate(reference, slots));

    [Fact]
    public void OverLimit_IsRejected()
    {
        Assert.Null(AppointmentRules.Validate("PT-1", AppointmentRules.MaxSlotsPerAppointment));
        Assert.NotNull(AppointmentRules.Validate("PT-1", AppointmentRules.MaxSlotsPerAppointment + 1));
        Assert.NotNull(AppointmentRules.Validate(new string('x', AppointmentRules.MaxReferenceLength + 1), 1));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData((long)int.MaxValue + 1)]
    [InlineData(4_294_967_297L)] // (int) of this wraps to 1; it must not pass as one Slots
    [InlineData(long.MaxValue)]
    public void IntegerOverflow_IsRejectedNotWrapped(long slots)
    {
        Assert.NotNull(AppointmentRules.Validate("PT-1", slots));
        Assert.Equal(1, unchecked((int)4_294_967_297L)); // referrals what the naive cast would have done
    }

    [Fact]
    public void HappyFee_IsClinicPerSlotsPlusDocumentationFee()
    {
        Assert.Null(AppointmentRules.Validate("PT-1", 3));
        Assert.Equal(3 * 12_000L + 1_500L, AppointmentRules.FeeCents(3));
        Assert.Equal(12_000L + 1_500L, AppointmentRules.FeeCents(1));
    }

    [Theory]
    [InlineData("POISON")]
    [InlineData("PT-POISON-7")]
    [InlineData("xxPOISONyy")]
    [InlineData("A-VERY-LONG-TOKEN-THAT-MERELY-CONTAINS-POISON-INSIDE")]
    public void PoisonSubstring_MatchesInsideLongerTokens(string reference) =>
        Assert.True(AppointmentRules.IsPoison(reference));

    [Theory]
    [InlineData("PT-1")]
    [InlineData("POISO")]
    [InlineData("poison")]
    public void NonPoison_DoesNotMatch(string reference) =>
        Assert.False(AppointmentRules.IsPoison(reference));
}
