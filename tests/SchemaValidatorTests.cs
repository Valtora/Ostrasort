using System.IO;
using System.Text.Json.Nodes;
using Ostrasort;
using Xunit;
using static Ostrasort.Tests.TestData;

namespace Ostrasort.Tests;

public class SchemaValidatorTests : IDisposable
{
    private readonly string _tmp;
    private readonly SchemaValidator _validator;

    public SchemaValidatorTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "ostrasort-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tmp, "schemas"));
        File.WriteAllText(Path.Combine(_tmp, "schemas", "conditions-schema.json"), """
            {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["strName"],
                "properties": {
                  "strName": { "type": "string" },
                  "nX": { "type": "integer" },
                  "strType": { "type": "string", "enum": ["A", "B"] }
                }
              }
            }
            """);
        var env = new GameEnv
        {
            GameRoot = _tmp, DiscoveredVia = "test", CoreDataDir = _tmp, ModsDir = _tmp,
        };
        _validator = new SchemaValidator(env);
    }

    public void Dispose() => Directory.Delete(_tmp, recursive: true);

    [Fact]
    public void ValidObject_NoErrors() =>
        Assert.Empty(_validator.Validate(Obj("""{"strName":"C","nX":3,"strType":"A"}"""), "conditions"));

    [Fact]
    public void UnknownProperty_IsRejected()
    {
        var errs = _validator.Validate(Obj("""{"strName":"C","bogus":true}"""), "conditions");
        Assert.Contains(errs, e => e.Contains("bogus"));
    }

    [Fact]
    public void MissingRequired_IsRejected()
    {
        var errs = _validator.Validate(Obj("""{"nX":1}"""), "conditions");
        Assert.Contains(errs, e => e.Contains("strName"));
    }

    [Fact]
    public void WrongType_IsRejected()
    {
        var errs = _validator.Validate(Obj("""{"strName":"C","nX":"not-a-number"}"""), "conditions");
        Assert.Contains(errs, e => e.Contains("nX"));
    }

    [Fact]
    public void BadEnum_IsRejected()
    {
        var errs = _validator.Validate(Obj("""{"strName":"C","strType":"Z"}"""), "conditions");
        Assert.Contains(errs, e => e.Contains("strType"));
    }

    [Fact]
    public void UnknownType_SkipsValidation() =>
        Assert.Empty(_validator.Validate(Obj("""{"strName":"C","anything":1}"""), "someTypeWithNoSchema"));
}
