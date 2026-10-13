using System.Text;

namespace bleak.Data.Anonymizer.Anonymizers;

/// <summary>A first/last name pair drawn from a recurring Simpsons character.</summary>
public readonly record struct SimpsonsCharacter(string First, string Last);

/// <summary>A per-row person: a Simpsons character plus a (Simpsons) middle name, kept coherent within a row.</summary>
public readonly record struct Persona(string First, string Middle, string Last)
{
    public string Full => $"{First} {Last}";
}

/// <summary>A per-row fake mailing address, Springfield-themed and obviously fictional.</summary>
public readonly record struct AddressPersona(
    string Line1, string City, string State, string Province, string Postal, string PostalExt);

/// <summary>
/// Curated pool of recurring Simpsons characters used to build obviously-fake — but realistically shaped —
/// names, usernames, and e-mail addresses. Everything here is fictional; the point is that a human glancing
/// at the anonymized data instantly recognizes it as fake (Bart Simpson, Ned Flanders, ...).
/// </summary>
public static class SimpsonsData
{
    /// <summary>Recurring characters with a clean first/last split.</summary>
    public static readonly IReadOnlyList<SimpsonsCharacter> Characters = new SimpsonsCharacter[]
    {
        new("Homer", "Simpson"), new("Marge", "Simpson"), new("Bart", "Simpson"),
        new("Lisa", "Simpson"), new("Maggie", "Simpson"), new("Abraham", "Simpson"),
        new("Ned", "Flanders"), new("Maude", "Flanders"), new("Rod", "Flanders"), new("Todd", "Flanders"),
        new("Moe", "Szyslak"), new("Barney", "Gumble"),
        new("Montgomery", "Burns"), new("Waylon", "Smithers"),
        new("Seymour", "Skinner"), new("Agnes", "Skinner"), new("Edna", "Krabappel"), new("Gary", "Chalmers"),
        new("Milhouse", "Van Houten"), new("Kirk", "Van Houten"), new("Luann", "Van Houten"),
        new("Nelson", "Muntz"), new("Jimbo", "Jones"), new("Dolph", "Starbeam"), new("Kearney", "Zzyzwicz"),
        new("Ralph", "Wiggum"), new("Clancy", "Wiggum"), new("Sarah", "Wiggum"),
        new("Apu", "Nahasapeemapetilon"), new("Manjula", "Nahasapeemapetilon"),
        new("Timothy", "Lovejoy"), new("Helen", "Lovejoy"),
        new("Julius", "Hibbert"), new("Nick", "Riviera"),
        new("Herschel", "Krustofski"), new("Troy", "McClure"), new("Lionel", "Hutz"), new("Kent", "Brockman"),
        new("Carl", "Carlson"), new("Lenny", "Leonard"),
        new("Patty", "Bouvier"), new("Selma", "Bouvier"), new("Otto", "Mann"),
        new("Jeff", "Albertson"), new("Snake", "Jailbird"), new("Tony", "DAmico"),
        new("Cletus", "Spuckler"), new("Brandine", "Spuckler"), new("Gil", "Gunderson"),
        new("Hans", "Moleman"), new("Rainier", "Wolfcastle"), new("Robert", "Terwilliger"),
        new("Jasper", "Beardley"), new("Disco", "Stu"), new("Groundskeeper", "Willie"),
    };

    /// <summary>Springfield-universe towns used for the fake "city" field.</summary>
    public static readonly IReadOnlyList<string> Cities = new[]
    {
        "Springfield", "Shelbyville", "Ogdenville", "North Haverbrook", "Capital City", "Cypress Creek",
    };

    /// <summary>Streets that appear in the show — the fake street addresses read as unmistakably Simpsons.</summary>
    public static readonly IReadOnlyList<string> Streets = new[]
    {
        "Evergreen Terrace", "Plympton Street", "Fake Street", "Elm Street", "Third Street",
        "Spalding Way", "Bourbon Street", "Main Street", "Highland Avenue",
    };

    private static readonly string[] UsStates = { "NT", "OH", "OR", "CA", "IL", "NY", "TX", "FL", "WA", "MA" };
    private static readonly string[] CaProvinces = { "ON", "QC", "BC", "AB", "MB", "NS" };

    public static SimpsonsCharacter RandomCharacter(Random rng) => Characters[rng.Next(Characters.Count)];

    public static Persona RandomPersona(Random rng)
    {
        var c = RandomCharacter(rng);
        var middle = RandomCharacter(rng).First; // a second Simpsons first name as the middle name
        return new Persona(c.First, middle, c.Last);
    }

    public static AddressPersona RandomAddress(Random rng)
    {
        var number = rng.Next(1, 9999);
        var line1 = $"{number} {Streets[rng.Next(Streets.Count)]}";
        var city = Cities[rng.Next(Cities.Count)];
        var state = UsStates[rng.Next(UsStates.Length)];
        var province = CaProvinces[rng.Next(CaProvinces.Length)];
        var postal = rng.Next(10000, 99999).ToString(); // 5-digit US-style ZIP
        var postalExt = rng.Next(0, 9999).ToString("D4");
        return new AddressPersona(line1, city, state, province, postal, postalExt);
    }

    /// <summary>
    /// Builds an e-mail local-part from a persona (e.g. "bart.simpson47"): lowercase, ASCII letters/digits
    /// and dots only, with a small random suffix so repeated names don't collide too obviously.
    /// </summary>
    public static string EmailLocalPart(Persona p, Random rng)
    {
        var raw = $"{p.First}.{p.Last}";
        var sb = new StringBuilder(raw.Length + 2);
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
            else if (ch == '.' || ch == ' ')
            {
                sb.Append('.');
            }
        }
        sb.Append(rng.Next(1, 100)); // 1..99
        return sb.ToString();
    }
}
