using System;
using System.Collections.Generic;
using System.Linq;

namespace MangaPipeline.Core;

public static class ConceptCandidateValidator
{
    private static readonly HashSet<string> ElementKinds = new(StringComparer.Ordinal)
    {
        "core-idea",
        "theme",
        "character",
        "setting",
        "conflict",
        "event",
        "scene",
        "panel",
        "motif"
    };

    private static readonly HashSet<string> RelationKinds = new(StringComparer.Ordinal)
    {
        "expresses",
        "embodies",
        "contrasts-with",
        "causes",
        "occurs-in",
        "involves",
        "appears-as"
    };

    public static void Validate(
        IReadOnlyCollection<ConceptCandidateElement> elements,
        IReadOnlyCollection<ConceptCandidateRelation> relations,
        IReadOnlyCollection<string> openQuestions)
    {
        if (elements is null || elements.Count == 0 ||
            relations is null ||
            openQuestions is null)
        {
            throw new WorkspaceException(
                WorkspaceErrorCode.Validation,
                "A concept candidate requires elements, relations, and open questions.");
        }

        var elementsById = new Dictionary<string, ConceptCandidateElement>(StringComparer.Ordinal);
        foreach (var element in elements)
        {
            if (element is null ||
                !IsIdentifier(element.Id) ||
                !ElementKinds.Contains(element.Kind) ||
                string.IsNullOrWhiteSpace(element.Label) ||
                !elementsById.TryAdd(element.Id, element))
            {
                throw new WorkspaceException(
                    WorkspaceErrorCode.Validation,
                    "Concept candidate elements must have unique ids, supported kinds, and labels.");
            }
        }

        if (!elements.Any(element => element.Kind == "core-idea"))
        {
            throw new WorkspaceException(
                WorkspaceErrorCode.Validation,
                "A concept candidate must contain a core-idea element.");
        }

        foreach (var relation in relations)
        {
            if (relation is null ||
                !RelationKinds.Contains(relation.Kind) ||
                !elementsById.ContainsKey(relation.FromElementId) ||
                !elementsById.ContainsKey(relation.ToElementId))
            {
                throw new WorkspaceException(
                    WorkspaceErrorCode.Validation,
                    "Concept candidate relations must use supported kinds and reference existing elements.");
            }
        }

        if (openQuestions.Any(string.IsNullOrWhiteSpace))
        {
            throw new WorkspaceException(
                WorkspaceErrorCode.Validation,
                "Concept candidate open questions cannot be empty.");
        }
    }

    private static bool IsIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value is not "." and not ".." &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}
