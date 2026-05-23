using System.Collections.Generic;

namespace Mapping_LIA.Services.Normalization;

public sealed class NormalizationOptions
{
    /// Fold diacritics (å ä ö a a o). Off by default to respect Swedish text.
    public bool FoldDiacritics { get; init; } = false;

    /// Remove stand-alone versions like "v2", "2.1", "version 3".
    public bool DropStandaloneVersions { get; init; } = true;

    /// Technologies for which we keep the version token.
    public string[] KeepVersionFor { get; init; } = new[]
    {
        ".net",
        "node.js",
        "react",
        "angular",
        "vue.js",
        "sql server"
    };

    /// Stopwords removed when they appear as separate tokens.
    public string[] Stopwords { get; init; } = new[]
    {
        "and","&","with","for","of","in","on","to",
        "och","med","för","av","i","på","till"
    };

    /// Alias map applied after cleanup. values should be lower-case.
    public Dictionary<string, string> Aliases { get; init; } = DefaultAliases();

    public static Dictionary<string, string> DefaultAliases() => new()
    {
        ["c sharp"] = "c#",
        ["c-sharp"] = "c#",
        ["c plus plus"] = "c++",
        ["c-plus-plus"] = "c++",
        ["objective c"] = "objective-c",
        ["objective c+"] = "objective-c",
        ["java script"] = "javascript",
        ["java-script"] = "javascript",
        ["js"] = "javascript",

        ["ts"] = "typescript",
        ["type script"] = "typescript",

        [".net core"] = ".net",
        ["dotnet"] = ".net",
        ["dot net"] = ".net",
        ["asp net"] = "asp.net",
        ["asp.net core"] = "asp.net",
        ["asp net core"] = "asp.net",
        ["asp.net mvc"] = "asp.net",
        ["asp net mvc"] = "asp.net",

        ["node js"] = "node.js",
        ["nodejs"] = "node.js",

        ["next js"] = "next.js",
        ["nextjs"] = "next.js",

        ["three js"] = "three.js",
        ["threejs"] = "three.js",

        ["reactjs"] = "react",
        ["react js"] = "react",
        ["react.js"] = "react",

        ["react native"] = "react-native",
        ["react-native"] = "react-native",

        ["angularjs"] = "angular",
        ["angular js"] = "angular",

        ["vue js"] = "vue.js",
        ["vuejs"] = "vue.js",

        ["html5"] = "html",
        ["css3"] = "css",

        ["ms sql"] = "sql server",
        ["mssql"] = "sql server",
        ["ms-sql"] = "sql server",
        ["sqlserver"] = "sql server",

        ["t sql"] = "t-sql",
        ["tsql"] = "t-sql",

        ["my sql"] = "mysql",
        ["my-sql"] = "mysql",

        ["postgre sql"] = "postgresql",
        ["postgre-sql"] = "postgresql",
        ["postgres"] = "postgresql",

        ["ms azure"] = "azure",
        ["microsoft azure"] = "azure",

        ["azure-devops"] = "azure devops",
        ["azure devops"] = "azure devops",
        ["ado"] = "azure devops",

        ["git hub"] = "github",
        ["github.com"] = "github",

        ["git lab"] = "gitlab",

        ["vs code"] = "visual studio code",
        ["vscode"] = "visual studio code",

        ["ms office"] = "microsoft office",
        ["office 365"] = "microsoft 365",
        ["o365"] = "microsoft 365",

        ["power-bi"] = "power bi",
        ["powerbi"] = "power bi",
        ["microsoft power bi"] = "power bi",

        ["cms"] = "content management system",
        ["dms"] = "document management system",
        ["qms"] = "quality management system",
        ["pm"] = "project management",
        ["proj mgmt"] = "project management",
        ["project mgmt"] = "project management",
        ["bi"] = "business intelligence",

        ["scrum-master"] = "scrum master",
        ["scrum master"] = "scrum master",

        ["projektledning"] = "project management",
        ["webbutveckling"] = "web development"
    };
}