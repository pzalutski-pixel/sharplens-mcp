using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace SharpLensMcp.Tests;

/// <summary>
/// Helper class for creating in-memory workspaces for testing RoslynService.
/// </summary>
public static class TestHelpers
{
    private static readonly MetadataReference[] CoreReferences = new[]
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(IAsyncEnumerable<>).Assembly.Location),
        MetadataReference.CreateFromFile(AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "System.Runtime").Location)
    };

    /// <summary>
    /// Creates an AdhocWorkspace with a single document containing the provided code.
    /// </summary>
    public static (AdhocWorkspace workspace, Document document) CreateWorkspaceWithCode(
        string code,
        string fileName = "Test.cs",
        string projectName = "TestProject")
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);

        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            projectName,
            projectName,
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: CoreReferences
        );

        var solution = workspace.CurrentSolution
            .AddProject(projectInfo)
            .AddDocument(documentId, fileName, SourceText.From(code));

        workspace.TryApplyChanges(solution);
        var document = workspace.CurrentSolution.GetDocument(documentId)!;

        return (workspace, document);
    }

    /// <summary>
    /// Creates a workspace with multiple documents.
    /// </summary>
    public static (AdhocWorkspace workspace, Document[] documents) CreateWorkspaceWithMultipleDocuments(
        params (string code, string fileName)[] files)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "TestProject",
            "TestProject",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest),
            metadataReferences: CoreReferences
        );

        var solution = workspace.CurrentSolution.AddProject(projectInfo);

        var documentIds = new List<DocumentId>();
        foreach (var (code, fileName) in files)
        {
            var documentId = DocumentId.CreateNewId(projectId);
            documentIds.Add(documentId);
            solution = solution.AddDocument(documentId, fileName, SourceText.From(code));
        }

        workspace.TryApplyChanges(solution);

        var documents = documentIds
            .Select(id => workspace.CurrentSolution.GetDocument(id)!)
            .ToArray();

        return (workspace, documents);
    }

    /// <summary>
    /// Gets the position (offset) in the code at the specified line and column (0-based).
    /// </summary>
    public static int GetPosition(string code, int line, int column)
    {
        var lines = code.Split('\n');
        var position = 0;
        for (var i = 0; i < line && i < lines.Length; i++)
        {
            position += lines[i].Length + 1; // +1 for newline
        }
        return position + column;
    }

    /// <summary>
    /// Finds the position of a specific text in the code.
    /// </summary>
    public static int FindTextPosition(string code, string text)
    {
        var index = code.IndexOf(text, StringComparison.Ordinal);
        if (index == -1)
            throw new ArgumentException($"Text '{text}' not found in code");
        return index;
    }

    /// <summary>
    /// Common sample code for testing various features.
    /// </summary>
    public static class SampleCode
    {
        public const string SimpleClass = @"
using System;

namespace TestNamespace
{
    public class Person
    {
        private string _name;
        private int _age;

        public Person(string name, int age)
        {
            _name = name;
            _age = age;
        }

        public string Name => _name;
        public int Age => _age;

        public void Greet()
        {
            Console.WriteLine($""Hello, I am {_name}"");
        }
    }
}";

        public const string InterfaceImplementation = @"
using System;

namespace TestNamespace
{
    public interface IAnimal
    {
        string Name { get; }
        void MakeSound();
        int GetAge();
    }

    public class Dog : IAnimal
    {
        public string Name => ""Dog"";

        public void MakeSound()
        {
            Console.WriteLine(""Woof!"");
        }

        public int GetAge() => 5;
    }
}";

        public const string ComplexMethod = @"
using System;
using System.Collections.Generic;

namespace TestNamespace
{
    public class Calculator
    {
        public int ComplexCalculation(int a, int b, string operation)
        {
            if (a < 0)
            {
                if (b < 0)
                {
                    return -1;
                }
                else
                {
                    return b;
                }
            }

            switch (operation)
            {
                case ""add"":
                    return a + b;
                case ""subtract"":
                    return a - b;
                case ""multiply"":
                    return a * b;
                case ""divide"":
                    if (b == 0)
                        throw new DivideByZeroException();
                    return a / b;
                default:
                    throw new ArgumentException(""Unknown operation"");
            }
        }

        public bool IsValid(int x) => x > 0 && x < 100 || x == -1;
    }
}";

        public const string ClassWithDiagnostics = @"
using System;

namespace TestNamespace
{
    public class BrokenClass
    {
        public void Method()
        {
            int unused = 42;
            string s = null;
            Console.WriteLine(s.Length);
        }
    }
}";

        public const string ClassNeedingNullChecks = @"
using System;

namespace TestNamespace
{
    public class Service
    {
        private readonly string _name;
        private readonly int _count;

        public Service(string name, int count, object data)
        {
            _name = name;
            _count = count;
        }

        public void Process(string input, object config)
        {
            Console.WriteLine(input);
        }
    }
}";

        public const string ClassForEquality = @"
using System;

namespace TestNamespace
{
    public class Point
    {
        public int X { get; }
        public int Y { get; }

        public Point(int x, int y)
        {
            X = x;
            Y = y;
        }
    }
}";

        public const string InheritanceHierarchy = @"
using System;

namespace TestNamespace
{
    public abstract class Shape
    {
        public abstract double Area { get; }
    }

    public class Circle : Shape
    {
        public double Radius { get; }
        public Circle(double radius) => Radius = radius;
        public override double Area => Math.PI * Radius * Radius;
    }

    public class Rectangle : Shape
    {
        public double Width { get; }
        public double Height { get; }
        public Rectangle(double w, double h) { Width = w; Height = h; }
        public override double Area => Width * Height;
    }

    public class Square : Rectangle
    {
        public Square(double side) : base(side, side) { }
    }
}";
    }
}
