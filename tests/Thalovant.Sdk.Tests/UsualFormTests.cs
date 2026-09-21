using Thalovant.Sdk;
using Xunit;

namespace Thalovant.Sdk.Tests;

/// <summary>
/// The tag to retry a listing with, when the hub had nothing under the one
/// asked for. These answers are CLDR's, not this SDK's, and must match the
/// Python reference: a managed port that disagreed about which language to
/// retry would list a different hub.
/// </summary>
public class UsualFormTests
{
    [Theory]
    [InlineData("en-CA", "en-us")]
    [InlineData("en-AT", "en-us")]
    [InlineData("fr-BE", "fr-fr")]
    [InlineData("pt-AO", "pt-br")]
    [InlineData("pt-PT", "pt-br")]
    [InlineData("de-AT", "de-de")]
    public void ARegionalTagBecomesTheFormSkillsRegister(string tag, string expected)
        => Assert.Equal(expected, ThalovantContext.UsualForm(tag));

    // Null rather than the same tag, so a hub that answered is never asked
    // twice.
    [Theory]
    [InlineData("en-US")]
    [InlineData("en-us")]
    [InlineData("fr-FR")]
    public void ATagAlreadyUsualHasNothingToRetryWith(string tag)
        => Assert.Null(ThalovantContext.UsualForm(tag));

    // Maximize does not fail on an unknown language: it walks down to "und"
    // and takes the root locale's region, so "zzz" would come back "zzz-us".
    [Theory]
    [InlineData("zzz")]
    [InlineData("")]
    [InlineData("xx-YY")]
    public void ALanguageNobodyHasHeardOfIsNullAndNotAGuess(string tag)
        => Assert.Null(ThalovantContext.UsualForm(tag));
}
