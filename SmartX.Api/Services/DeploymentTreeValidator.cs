using SmartX.Api.Models;

namespace SmartX.Api.Services;

public sealed record DeploymentValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors);

public sealed class DeploymentTreeValidator
{
    public DeploymentValidationResult Validate(DeploymentNode root)
    {
        List<string> errors = [];

        ValidateNode(root, null, 0, errors);

        return new DeploymentValidationResult(
            errors.Count == 0,
            errors);
    }

    private static void ValidateNode(
        DeploymentNode node,
        DeploymentLevel? parentLevel,
        int depth,
        List<string> errors)
    {
        if (depth > 4)
        {
            errors.Add("The deployment hierarchy is too deep.");
            return;
        }

        if (string.IsNullOrWhiteSpace(node.Name))
        {
            errors.Add($"A {node.Level} name is required.");
        }

        if (parentLevel is not null &&
            !IsAllowedChild(parentLevel.Value, node.Level))
        {
            errors.Add(
                $"{node.Level} cannot be placed below {parentLevel.Value}.");
        }

        if (node.Level == DeploymentLevel.Node &&
            node.Children.Count > 0)
        {
            errors.Add("A device node cannot contain child locations.");
            return;
        }

        if (node.Children.Count == 0)
        {
            if (node.Level != DeploymentLevel.Node)
            {
                errors.Add($"{node.Level} must contain a child location.");
            }

            return;
        }

        foreach (var child in node.Children)
        {
            ValidateNode(child, node.Level, depth + 1, errors);
        }
    }

    private static bool IsAllowedChild(
        DeploymentLevel parent,
        DeploymentLevel child)
    {
        return parent switch
        {
            DeploymentLevel.Facility =>
                child == DeploymentLevel.Zone,

            DeploymentLevel.Zone =>
                child is DeploymentLevel.SubZone
                    or DeploymentLevel.Node,

            DeploymentLevel.SubZone =>
                child == DeploymentLevel.Node,

            _ => false
        };
    }
}