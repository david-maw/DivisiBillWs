namespace DivisiBillWs;


// This is a primitive 'traits' style implementation which could be improved upon since C#8, see 
// https://stackoverflow.com/questions/10729230/how-would-you-implement-a-trait-design-pattern-in-c
public class StorageClass
{
    public string TableName = "";
    public bool UseSummaryField = false;
    public bool CheckImage = false;
}

internal class MealStorage : StorageClass
{
    public MealStorage()
    {
        TableName = "Meal";
        UseSummaryField = true;
        CheckImage = true; // Meals may have images, so we need to check if they are present
    }
}
internal class VenueListStorage : StorageClass
{
    public VenueListStorage() => TableName = "VenueList";
}
internal class PersonListStorage : StorageClass
{
    public PersonListStorage() => TableName = "PersonList";
}
