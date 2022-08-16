using NBitcoin;
using System.Collections.Generic;
using System.Linq;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.Transactions;

namespace WalletWasabi.Blockchain.Analysis;

public class CoinjoinAnalyzer
{
	public CoinjoinAnalyzer(SmartTransaction transaction)
	{
		AnalyzedTransaction = transaction;
		AnalyzedTransactionPrevOuts = AnalyzedTransaction.Transaction.Inputs.Select(input => input.PrevOut).ToHashSet();
	}

	private HashSet<OutPoint> AnalyzedTransactionPrevOuts { get; }
	private Dictionary<SmartCoin, double> CachedInputSanctions { get; } = new();

	public SmartTransaction AnalyzedTransaction { get; }

	public double ComputeInputSanction(WalletVirtualInput virtualInput)
		=> virtualInput.Coins.Select(ComputeInputSanction).Max();

	public double ComputeInputSanction(SmartCoin transactionInput)
	{
		double ComputeInputSanctionHelper(SmartCoin transactionOutput)
		{
			// If we already analyzed the sanction for this output, then return the cached result.
			if (CachedInputSanctions.ContainsKey(transactionOutput))
			{
				return CachedInputSanctions[transactionOutput];
			}

			// Look at the transaction containing transactionOutput.
			// We are searching for any transaction inputs of analyzedTransaction that might have come from this transaction.
			// If we find such remixed outputs, then we determine how much they contributed to our anonymity set.
			SmartTransaction transaction = transactionOutput.Transaction;
			double sanction = ComputeAnonymityContribution(transactionOutput, AnalyzedTransactionPrevOuts);

			// Recursively branch out into all of the transaction inputs' histories and compute the sanction for each branch.
			// Add the worst-case branch to the resulting sanction.
			sanction += transaction.WalletInputs.Select(ComputeInputSanctionHelper).DefaultIfEmpty(0).Max();

			// Cache the computed sanction in case we need it later.
			CachedInputSanctions[transactionOutput] = sanction;
			return sanction;
		}

		return ComputeInputSanctionHelper(transactionInput);
	}

	/// <summary>
	/// Computes how much the foreign outputs of AnalyzedTransaction contribute to the anonymity of our transactionOutput.
	/// Sometimes we are only interested in how much a certain subset of foreign outputs contributed.
	/// This subset can be specified in relevantOutpoints, otherwise all outputs are considered relevant.
	/// </summary>
	public static double ComputeAnonymityContribution(SmartCoin transactionOutput, HashSet<OutPoint>? relevantOutpoints = null)
	{
		SmartTransaction transaction = transactionOutput.Transaction;
		IEnumerable<WalletVirtualOutput> walletVirtualOutputs = transaction.WalletVirtualOutputs;
		IEnumerable<ForeignVirtualOutput> foreignVirtualOutputs = transaction.ForeignVirtualOutputs;

		Money amount = walletVirtualOutputs.Where(o => o.Coins.Select(c => c.OutPoint).Contains(transactionOutput.OutPoint)).First().Amount;
		bool IsRelevantVirtualOutput(ForeignVirtualOutput output) => relevantOutpoints is null || relevantOutpoints.Intersect(output.OutPoints).Any();

		// Count the outputs that have the same value as our transactionOutput.
		var equalValueWalletVirtualOutputCount = walletVirtualOutputs.Where(o => o.Amount == amount).Count();
		var equalValueForeignRelevantVirtualOutputCount = foreignVirtualOutputs.Where(o => o.Amount == amount).Where(IsRelevantVirtualOutput).Count();

		// The anonymity set should increase by the number of equal-valued foreign ouputs.
		// If we have multiple equal-valued wallet outputs, then we divide the increase evenly between them.
		// The rationale behind this is that picking randomly an output would make our anonset:
		// total/ours = 1 + foreign/ours, so the increase in anonymity is foreign/ours.
		return (double)equalValueForeignRelevantVirtualOutputCount / equalValueWalletVirtualOutputCount;
	}
}