using DbScrub.Core.Reporting;
using Xunit;

namespace DbScrub.Tests.Reporting;

/// <summary>
/// The name-based guess that decides which columns the summarised report leads
/// with (DECISIONS.md D29).
///
/// Every example here is a real column name from the aavsb schema, 2,958 columns
/// across 220 tables. The false positives are the point: they are what a naive
/// substring search produces, and they are why the matching works on whole
/// camel-case tokens instead.
/// </summary>
public class PersonalDataHintsTests
{
    // ---- the failure that motivated token matching -------------------------

    [Fact]
    public void KnownCapacityIsNotAnAddress()
    {
        // A substring search for "city" flags this. It is a number of places in
        // a training programme. This single column is the reason for the
        // tokenizer.
        Assert.Null(PersonalDataHints.Describe("KnownCapacity"));
    }

    [Theory]
    [InlineData("MailingCity", "Mailing", "City")]
    [InlineData("SSNLastFour", "SSN", "Last", "Four")]
    [InlineData("DOB", "DOB")]
    [InlineData("mailing_city", "mailing", "city")]
    [InlineData("Address2", "Address2")]
    public void NamesSplitIntoWholeWords(string name, params string[] expected)
    {
        Assert.Equal(expected, PersonalDataHints.Tokenize(name));
    }

    // ---- what it should catch ----------------------------------------------

    [Theory]
    [InlineData("SSN")]
    [InlineData("SSNLastFour")]
    [InlineData("DOB")]
    [InlineData("DateOfBirth")]
    [InlineData("PlaceOfBirth")]
    [InlineData("Email")]
    [InlineData("LoweredEmail")]
    [InlineData("HomePhone")]
    [InlineData("RegistrarFax")]
    [InlineData("MailingAddress")]
    [InlineData("MailingZip")]
    [InlineData("Password")]
    [InlineData("FirstName")]
    [InlineData("OtherLastNameUsed")]
    [InlineData("ContactMiddleName")]
    public void RealPersonalColumnsAreFlaggedStrongly(string name)
    {
        var hint = PersonalDataHints.Describe(name);

        Assert.NotNull(hint);
        Assert.True(hint.Strong, $"expected '{name}' to be a strong hint, got '{hint.Description}'");
    }

    // ---- what it must NOT catch --------------------------------------------

    [Theory]
    [InlineData("ShouldReceiveDisciplineNotificationEmails")]
    [InlineData("SendScoreTransferByFax")]
    [InlineData("SendCEDataByEmail")]
    [InlineData("UsePodEmailForLVNotifications")]
    [InlineData("AttachPhysicalFilesToCEEmail")]
    public void YesNoSettingsAreNotPersonalData(string name)
    {
        // These say HOW to contact someone, never who they are. Masking one
        // would change application behaviour while looking like progress.
        Assert.Null(PersonalDataHints.Describe(name));
    }

    [Theory]
    [InlineData("EmailType")]
    [InlineData("EmailTypeDescription")]
    [InlineData("EmailsId")]
    [InlineData("EmailSentLogId")]
    [InlineData("EmailSentDate")]
    [InlineData("AddressID")]
    [InlineData("AddressTypeID")]
    [InlineData("PasswordFormat")]
    [InlineData("LicenseStatusID")]
    public void RecordMetadataIsNotPersonalData(string name)
    {
        Assert.Null(PersonalDataHints.Describe(name));
    }

    // ---- the qualified cases -----------------------------------------------

    [Fact]
    public void BareNameIsOnlyAWeakHint()
    {
        // The single most common column name in the measured schema, and most of
        // them name a status or a document type rather than a person.
        var hint = PersonalDataHints.Describe("Name");

        Assert.NotNull(hint);
        Assert.False(hint.Strong);
    }

    [Fact]
    public void AQualifierTurnsNameIntoAPerson()
    {
        Assert.True(PersonalDataHints.Describe("LastName")!.Strong);
    }

    [Fact]
    public void LicenseNumberIsPersonalButLicenseTypeIsNot()
    {
        // "License" alone is a category. Paired with "Number" it identifies a
        // practitioner — which is why the compound is checked before the
        // metadata suppression rather than after.
        Assert.True(PersonalDataHints.Describe("LicenseNumber")!.Strong);
        Assert.Null(PersonalDataHints.Describe("LicenseType"));
    }

    [Fact]
    public void FreeTextIsFlaggedButOnlyWeakly()
    {
        // No scan can tell whether a Notes column holds a phone number. Only the
        // person who runs the application knows, so this asks rather than tells.
        var hint = PersonalDataHints.Describe("DisciplineNotes");

        Assert.NotNull(hint);
        Assert.False(hint.Strong);
        Assert.Contains("free text", hint.Description);
    }

    [Theory]
    [InlineData("VeterinarianId")]
    [InlineData("CreatedUtc")]
    [InlineData("SortOrder")]
    [InlineData("")]
    public void OrdinaryColumnsProduceNoHint(string name)
    {
        Assert.Null(PersonalDataHints.Describe(name));
    }
}
