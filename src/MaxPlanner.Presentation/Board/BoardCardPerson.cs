using MaxPlanner.Application.ReadModels;
using MaxPlanner.Domain.Identifiers;
using MaxPlanner.Presentation.Common.Projections;
using MaxPlanner.Presentation.Common.Services;

namespace MaxPlanner.Presentation.Board;

/// <summary>
/// One avatar in a Board card's people footer (design §4.2 item 9): a person once, with every role they hold on
/// the card. Value equality lets the card skip a change notification when an update leaves its people unchanged.
/// </summary>
/// <param name="RolesText">"CR Owner · Designer": the tooltip's secondary line.</param>
/// <param name="AccessibleName">"Mina Hakim, CR Owner, Designer".</param>
public sealed record BoardCardPerson(PersonId PersonId, string Name, string RolesText, string AccessibleName, string AutomationId)
{
    /// <summary>
    /// The people of a card in role order CR Owner, Designer, Developer, each person once with their roles in that
    /// order. A Subtask card has only the inherited CR Owner.
    /// </summary>
    public static IReadOnlyList<BoardCardPerson> From(WorkItemRecord record, ILocalizedStrings strings)
    {
        ArgumentNullException.ThrowIfNull(record);
        var owner = record.Cr is { OwnerId: { } ownerId, OwnerName: { } ownerName } ? new LookupRef<PersonId>(ownerId, ownerName) : null;
        return Build(record.Id, owner, record.Task?.Designer, record.Task?.Developer, strings);
    }

    /// <summary>Groups the assigned roles by person; a missing role is skipped.</summary>
    public static IReadOnlyList<BoardCardPerson> Build(
        Guid cardKey, LookupRef<PersonId>? owner, LookupRef<PersonId>? designer, LookupRef<PersonId>? developer, ILocalizedStrings strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        (LookupRef<PersonId>? Person, string RoleKey)[] assignments =
        [
            (owner, "Board_PersonRoleCrOwner"),
            (designer, "Field_Designer"),
            (developer, "Field_Developer"),
        ];

        var people = new List<(LookupRef<PersonId> Person, List<string> Roles)>(3);
        foreach (var (person, roleKey) in assignments)
        {
            if (person is null)
            {
                continue;
            }

            var role = strings.GetString(roleKey);
            var index = people.FindIndex(p => p.Person.Id == person.Id);
            if (index < 0)
            {
                people.Add((person, [role]));
            }
            else
            {
                people[index].Roles.Add(role);
            }
        }

        // A plain array: it reaches ItemsRepeater.ItemsSource, which takes a WinRT-projectable list.
        return people.Count == 0
            ? Array.Empty<BoardCardPerson>()
            : people.Select(p => new BoardCardPerson(
                p.Person.Id,
                p.Person.Name,
                JoinRoles(p.Roles, strings),
                NameWithRoles(p.Person.Name, p.Roles, strings),
                $"Board_CardPerson_{cardKey:N}_{p.Person.Id.Value:N}")).ToArray();
    }

    private static string JoinRoles(List<string> roles, ILocalizedStrings strings) => roles.Count switch
    {
        1 => roles[0],
        2 => strings.Format("Board_PersonRoles2", roles[0], roles[1]),
        _ => strings.Format("Board_PersonRoles3", roles[0], roles[1], roles[2]),
    };

    private static string NameWithRoles(string name, List<string> roles, ILocalizedStrings strings) => roles.Count switch
    {
        1 => strings.Format("Board_PersonName1", name, roles[0]),
        2 => strings.Format("Board_PersonName2", name, roles[0], roles[1]),
        _ => strings.Format("Board_PersonName3", name, roles[0], roles[1], roles[2]),
    };
}
