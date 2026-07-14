using FluentAssertions;
using FluentValidation.TestHelper;
using WireHQ.Application.Features.Authentication.Register;
using Xunit;

namespace WireHQ.Application.UnitTests.Authentication;

public sealed class RegisterCommandValidatorTests
{
    private readonly RegisterCommandValidator _validator = new();

    [Fact]
    public void Accepts_a_valid_command()
    {
        var command = new RegisterCommand("ada@wirehq.io", "Sup3rSecret!!", "Ada", "Lovelace", AcceptTerms: true);

        _validator.TestValidate(command).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("short1A")]      // too short
    [InlineData("alllowercase1")] // no uppercase
    [InlineData("NODIGITSHERE!")] // no digit
    public void Rejects_weak_passwords(string password)
    {
        var command = new RegisterCommand("ada@wirehq.io", password, "Ada", "Lovelace", AcceptTerms: true);

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(c => c.Password);
    }

    [Fact]
    public void Rejects_invalid_email()
    {
        var command = new RegisterCommand("not-an-email", "Sup3rSecret!!", "Ada", "Lovelace", AcceptTerms: true);

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(c => c.Email);
    }

    [Fact]
    public void Requires_accepting_the_terms()
    {
        var command = new RegisterCommand("ada@wirehq.io", "Sup3rSecret!!", "Ada", "Lovelace", AcceptTerms: false);

        _validator.TestValidate(command).ShouldHaveValidationErrorFor(c => c.AcceptTerms);
    }

    [Fact]
    public void Requires_first_and_last_name()
    {
        var command = new RegisterCommand("ada@wirehq.io", "Sup3rSecret!!", "", "", AcceptTerms: true);

        var result = _validator.TestValidate(command);
        result.ShouldHaveValidationErrorFor(c => c.FirstName);
        result.ShouldHaveValidationErrorFor(c => c.LastName);
    }
}
