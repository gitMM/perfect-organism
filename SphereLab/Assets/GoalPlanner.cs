using System;
using System.Collections.Generic;

// Forward GOAP over procedural actions. The caller simulates a successor
// before offering it; its world snapshot is the action's actual effect.
public sealed class GoalPlanner<T> where T:class
{
    readonly Func<T,int> depth,firstAction;
    readonly Func<T,float> estimate,cost;
    readonly Func<T,bool> satisfies;
    public T Best {get;private set;}
    public string StopReason {get;private set;}

    public GoalPlanner(Func<T,int> depth,Func<T,int> firstAction,Func<T,float> estimate,Func<T,float> cost,Func<T,bool> satisfies)
    {
        this.depth=depth;this.firstAction=firstAction;this.estimate=estimate;this.cost=cost;this.satisfies=satisfies;
    }

    public void Search(T root,Func<T,Action<T>,bool> expand,Func<bool> timeLeft,int horizon,int budget,bool realtime)
    {
        Best=null;
        var open=new List<T>{root};
        var represented=new HashSet<int>();
        int layer=-1,layerExpanded=0,expanded=0;
        void Offer(T next){
            bool wins=satisfies(next);
            if(Best==null || (wins && (!satisfies(Best) || cost(next)<cost(Best))) ||
                (!wins && !satisfies(Best) && (depth(next)>depth(Best) ||
                (depth(next)==depth(Best) && estimate(next)<estimate(Best)))))Best=next;
            if(!wins && depth(next)<horizon)open.Add(next);
        }
        while(open.Count>0 && expanded<budget && timeLeft()){
            int level=depth(open[0]);
            if(level!=layer){layer=level;layerExpanded=0;represented.Clear();}
            if(!realtime && level>0 && layerExpanded>=(budget-1)/Math.Max(1,horizon-1)){
                open.RemoveAll(n=>depth(n)==level);continue;
            }
            int k=0;
            if(realtime && (Best==null || depth(Best)<horizon)){
                for(int i=1;i<open.Count;i++)
                    if(depth(open[i])>depth(open[k]) || (depth(open[i])==depth(open[k]) && estimate(open[i])<estimate(open[k])))k=i;
            }else{
                for(int i=1;i<open.Count && depth(open[i])==level;i++){
                    bool fresh=!represented.Contains(firstAction(open[i])),current=!represented.Contains(firstAction(open[k]));
                    if((fresh && !current) || (fresh==current && estimate(open[i])<estimate(open[k])))k=i;
                }
            }
            var state=open[k];open.RemoveAt(k);represented.Add(firstAction(state));layerExpanded++;expanded++;
            // A live expansion can yield after a promising child. Keep the
            // parent available so its remaining actions can still compete.
            if(!expand(state,Offer))open.Insert(0,state);
            if(Best!=null && satisfies(Best))break;
        }
        StopReason=Best!=null && satisfies(Best)?"Goal reached":
            expanded>=budget?"State limit":open.Count==0?"Frontier exhausted":"Time limit";
    }
}
