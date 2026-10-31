using CUE4Parse.UE4.Kismet;
using static Prism.BlueprintGraph.Engine.EngineBPData;
using static Prism.BlueprintGraph.Engine.Utils;

namespace Prism.BlueprintGraph.Nodes
{
    internal class K2Node_ClassDynamicCast : K2Node_DynamicCast
    {
        public K2Node_ClassDynamicCast(GraphPin ObjectPin, GraphPin AsObjectPin, string ClassName, KismetExpression Instr) : base(ObjectPin, AsObjectPin, ClassName, Instr)
        {
            GraphPinType ClassType = MakePinType(PinType.Class);
            this.AsObjectPin.PinType = ClassType;
            this.ObjectPin.PinType = ClassType;
            this.ObjectPin.SetName("Class");
            Name = $"Cast To {ClassName} Class";
        }
    }
}
