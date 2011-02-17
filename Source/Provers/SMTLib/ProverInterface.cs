//-----------------------------------------------------------------------------
//
// Copyright (C) Microsoft Corporation.  All Rights Reserved.
//
//-----------------------------------------------------------------------------
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.IO;
//using ExternalProver;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using Microsoft.Boogie.AbstractInterpretation;
using Microsoft.Boogie;
using Microsoft.Boogie.VCExprAST;
using Microsoft.Boogie.Clustering;
using Microsoft.Boogie.TypeErasure;
using Microsoft.Boogie.Simplify;

namespace Microsoft.Boogie.SMTLib
{
  public class SMTLibProverOptions : ProverOptions
  {
    public string Output = "boogie-vc-@PROC@.smt2";
    public bool UseWeights = true;
    public bool UseLabels { get { return UseZ3; } }
    public bool UseZ3 = true;

    protected override bool Parse(string opt)
    {
      return
        ParseString(opt, "OUTPUT", ref Output) ||
        ParseBool(opt, "USE_WEIGHTS", ref UseWeights) ||
        ParseBool(opt, "USE_Z3", ref UseZ3) ||
        base.Parse(opt);
    }

    public override string Help
    {
      get
      {
        return
@"
SMT-specific options:
~~~~~~~~~~~~~~~~~~~~~
OUTPUT=<string>           Store VC in named file (default: boogie-vc-@PROC@.smt2)
USE_WEIGHTS=<bool>        Pass :weight annotations on quantified formulas (default: true)
USE_Z3=<bool>             Use z3.exe as the prover, and use Z3 extensions (default: true)
" + base.Help;
        // DIST requires non-public binaries
      }
    }
  }

  public class SMTLibProcessTheoremProver : ProverInterface
  {
    private readonly DeclFreeProverContext ctx;
    private readonly VCExpressionGenerator gen;
    private readonly SMTLibProverOptions options;

    [ContractInvariantMethod]
    void ObjectInvariant()
    {
      Contract.Invariant(ctx != null);
      Contract.Invariant(AxBuilder != null);
      Contract.Invariant(Namer != null);
      Contract.Invariant(DeclCollector != null);
      Contract.Invariant(cce.NonNullElements(Axioms));
      Contract.Invariant(cce.NonNullElements(TypeDecls));
      Contract.Invariant(_backgroundPredicates != null);

    }


    [NotDelayed]
    public SMTLibProcessTheoremProver(ProverOptions options, VCExpressionGenerator gen,
                                      DeclFreeProverContext ctx)
    {
      Contract.Requires(options != null);
      Contract.Requires(gen != null);
      Contract.Requires(ctx != null);
      InitializeGlobalInformation("UnivBackPred2.smt2");

      this.options = (SMTLibProverOptions)options;
      this.ctx = ctx;
      this.gen = gen;

      TypeAxiomBuilder axBuilder;
      switch (CommandLineOptions.Clo.TypeEncodingMethod) {
        case CommandLineOptions.TypeEncoding.Arguments:
          axBuilder = new TypeAxiomBuilderArguments(gen);
          axBuilder.Setup();
          break;
        case CommandLineOptions.TypeEncoding.Monomorphic:
          axBuilder = new TypeAxiomBuilderPremisses(gen);
          break;
        default:
          axBuilder = new TypeAxiomBuilderPremisses(gen);
          axBuilder.Setup();
          break;
      }

      AxBuilder = axBuilder;
      Namer = new SMTLibNamer();
      this.DeclCollector = new TypeDeclCollector((SMTLibProverOptions)options, Namer);

    }

    public override ProverContext Context
    {
      get
      {
        Contract.Ensures(Contract.Result<ProverContext>() != null);

        return ctx;
      }
    }

    private readonly TypeAxiomBuilder AxBuilder;
    private readonly UniqueNamer Namer;
    private readonly TypeDeclCollector DeclCollector;
    private readonly SMTLibProcess Process;

    private void FeedTypeDeclsToProver()
    {
      foreach (string s in DeclCollector.GetNewDeclarations()) {
        Contract.Assert(s != null);
        AddTypeDecl(s);
      }
    }

    public override void BeginCheck(string descriptiveName, VCExpr vc, ErrorHandler handler)
    {
      //Contract.Requires(descriptiveName != null);
      //Contract.Requires(vc != null);
      //Contract.Requires(handler != null);
      TextWriter output = OpenOutputFile(descriptiveName);
      Contract.Assert(output != null);

      WriteLineAndLog(output, _backgroundPredicates);
      string name = SMTLibNamer.QuoteId(descriptiveName);
      WriteLineAndLog(output, "(set-info :boogie-vc-id " + name + ")");

      if (!AxiomsAreSetup) {
        var axioms = ctx.Axioms;
        var nary = axioms as VCExprNAry;
        if (nary != null && nary.Op == VCExpressionGenerator.AndOp)
          foreach (var expr in nary.UniformArguments) {
            var str = VCExpr2String(expr, -1);
            if (str != "true")
              AddAxiom(str);
          } else
          AddAxiom(VCExpr2String(axioms, -1));
        AxiomsAreSetup = true;
      }

      string vcString = "(assert (not\n" + VCExpr2String(vc, 1) + "\n))";
      string prelude = ctx.GetProverCommands(true);
      Contract.Assert(prelude != null);
      WriteLineAndLog(output, prelude);

      foreach (string s in TypeDecls) {
        Contract.Assert(s != null);
        WriteLineAndLog(output, s);
      }
      foreach (string s in Axioms) {
        Contract.Assert(s != null);
        if (s != "true")
          WriteLineAndLog(output, "(assert " + s + ")");
      }

      WriteLineAndLog(output, vcString);
      WriteLineAndLog(output, "(check-sat)");

      output.Close();
    }

    private TextWriter OpenOutputFile(string descriptiveName)
    {
      Contract.Requires(descriptiveName != null);
      Contract.Ensures(Contract.Result<TextWriter>() != null);

      string filename = ((SMTLibProverOptions)options).Output;
      filename = Helpers.SubstituteAtPROC(descriptiveName, cce.NonNull(filename));
      return new StreamWriter(filename, false);
    }

    private void WriteLineAndLog(TextWriter output, string msg)
    {
      Contract.Requires(output != null);
      Contract.Requires(msg != null);
      var idx = msg.IndexOf('\n');
      if (idx > 0) {
        msg = msg.Replace("\r", "").Replace("\n", "\r\n");
      }
      output.WriteLine(msg);
    }

    [NoDefaultContract]
    public override Outcome CheckOutcome(ErrorHandler handler)
    {  //Contract.Requires(handler != null);
      Contract.EnsuresOnThrow<UnexpectedProverOutputException>(true);
      return Outcome.Undetermined;
    }

    protected string VCExpr2String(VCExpr expr, int polarity)
    {
      Contract.Requires(expr != null);
      Contract.Ensures(Contract.Result<string>() != null);

      DateTime start = DateTime.Now;
      //if (CommandLineOptions.Clo.Trace)
      //  Console.Write("Linearising ... ");

      // handle the types in the VCExpr
      TypeEraser eraser;
      switch (CommandLineOptions.Clo.TypeEncodingMethod) {
        case CommandLineOptions.TypeEncoding.Arguments:
          eraser = new TypeEraserArguments((TypeAxiomBuilderArguments)AxBuilder, gen);
          break;
        case CommandLineOptions.TypeEncoding.Monomorphic:
          eraser = null;
          break;
        default:
          eraser = new TypeEraserPremisses((TypeAxiomBuilderPremisses)AxBuilder, gen);
          break;
      }
      VCExpr exprWithoutTypes = eraser == null ? expr : eraser.Erase(expr, polarity);
      Contract.Assert(exprWithoutTypes != null);

      LetBindingSorter letSorter = new LetBindingSorter(gen);
      Contract.Assert(letSorter != null);
      VCExpr sortedExpr = letSorter.Mutate(exprWithoutTypes, true);
      Contract.Assert(sortedExpr != null);
      VCExpr sortedAxioms = letSorter.Mutate(AxBuilder.GetNewAxioms(), true);
      Contract.Assert(sortedAxioms != null);

      DeclCollector.Collect(sortedAxioms);
      DeclCollector.Collect(sortedExpr);
      FeedTypeDeclsToProver();

      AddAxiom(SMTLibExprLineariser.ToString(sortedAxioms, Namer, options));
      string res = SMTLibExprLineariser.ToString(sortedExpr, Namer, options);
      Contract.Assert(res != null);

      if (CommandLineOptions.Clo.Trace) {
        DateTime end = DateTime.Now;
        TimeSpan elapsed = end - start;
        if (elapsed.TotalSeconds > 0.5)
          Console.WriteLine("Linearising   [{0} s]", elapsed.TotalSeconds);
      }
      return res;
    }

    // the list of all known axioms, where have to be included in each
    // verification condition
    private readonly List<string/*!>!*/> Axioms = new List<string/*!*/>();
    private bool AxiomsAreSetup = false;




    // similarly, a list of function/predicate declarations
    private readonly List<string/*!>!*/> TypeDecls = new List<string/*!*/>();

    protected void AddAxiom(string axiom)
    {
      Contract.Requires(axiom != null);
      Axioms.Add(axiom);
      //      if (thmProver != null) {
      //        LogActivity(":assume " + axiom);
      //        thmProver.AddAxioms(axiom);
      //      }
    }

    protected void AddTypeDecl(string decl)
    {
      Contract.Requires(decl != null);
      TypeDecls.Add(decl);
      //     if (thmProver != null) {
      //       LogActivity(decl);
      //       thmProver.Feed(decl, 0);
      //     }
    }

    ////////////////////////////////////////////////////////////////////////////

    private static string _backgroundPredicates;

    static void InitializeGlobalInformation(string backgroundPred)
    {
      Contract.Requires(backgroundPred != null);
      Contract.Ensures(_backgroundPredicates != null);
      //throws ProverException, System.IO.FileNotFoundException;
      if (_backgroundPredicates == null) {
        string codebaseString =
          cce.NonNull(Path.GetDirectoryName(cce.NonNull(System.Reflection.Assembly.GetExecutingAssembly().Location)));

        // Initialize '_backgroundPredicates'
        string univBackPredPath = Path.Combine(codebaseString, backgroundPred);
        using (StreamReader reader = new System.IO.StreamReader(univBackPredPath)) {
          _backgroundPredicates = reader.ReadToEnd();
        }
      }
    }

    public override VCExpressionGenerator VCExprGen
    {
      get { return this.gen; }
    }
  }

  public class Factory : ProverFactory
  {

    public override object SpawnProver(ProverOptions options, object ctxt)
    {
      //Contract.Requires(ctxt != null);
      //Contract.Requires(options != null);
      Contract.Ensures(Contract.Result<object>() != null);

      return this.SpawnProver(options,
                              cce.NonNull((DeclFreeProverContext)ctxt).ExprGen,
                              cce.NonNull((DeclFreeProverContext)ctxt));
    }

    public override object NewProverContext(ProverOptions options)
    {
      //Contract.Requires(options != null);
      Contract.Ensures(Contract.Result<object>() != null);

      VCExpressionGenerator gen = new VCExpressionGenerator();
      List<string>/*!>!*/ proverCommands = new List<string/*!*/>();
      // TODO: what is supported?
      //      proverCommands.Add("all");
      //      proverCommands.Add("simplify");
      //      proverCommands.Add("simplifyLike");
      VCGenerationOptions genOptions = new VCGenerationOptions(proverCommands);
      Contract.Assert(genOptions != null);

      return new DeclFreeProverContext(gen, genOptions);
    }

    public override ProverOptions BlankProverOptions()
    {
      return new SMTLibProverOptions();
    }

    protected virtual SMTLibProcessTheoremProver SpawnProver(ProverOptions options,
                                                              VCExpressionGenerator gen,
                                                              DeclFreeProverContext ctx)
    {
      Contract.Requires(options != null);
      Contract.Requires(gen != null);
      Contract.Requires(ctx != null);
      Contract.Ensures(Contract.Result<SMTLibProcessTheoremProver>() != null);

      return new SMTLibProcessTheoremProver(options, gen, ctx);
    }
  }
}
