using CUE4Parse.UE4.Kismet;
using System.Collections.Generic;
using static Prism.BlueprintGraph.Engine.EngineBPData;
using static Prism.BlueprintGraph.Engine.EngineEnums;
using static Prism.BlueprintGraph.Engine.Utils;

namespace Prism.BlueprintGraph.Nodes
{
    public class K2Node_MacroInstance : BPNode
    {
        List<GraphPin> InputPins;
        List<GraphPin> OutputPins;

        public K2Node_MacroInstance(List<GraphPin> InputPins, List<GraphPin> OutputPins, string name, KismetExpression Instr) : base("", Instr)
        {
            Name = name;
            this.InputPins = InputPins;
            this.OutputPins = OutputPins;
            MakePins(true, true);
        }

        protected override void MakePins(bool needExec, bool needThen)
        {
            foreach (GraphPin pin in InputPins)
                AddInputPin(pin);

            foreach (GraphPin pin in OutputPins)
                AddOutputPin(pin);
        }
    }
}
