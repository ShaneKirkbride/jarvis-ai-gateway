# IAM Matrix

| Principal | Permission | Resource | Notes |
| --- | --- | --- | --- |
| ECS execution role | ECR pull, CloudWatch log delivery | ECR repo, log group | Standard Fargate execution role permissions. |
| ECS task role | `bedrock:InvokeModel` | Approved foundation-model ARNs or inference-profile ARNs | Required for non-streaming completions. |
| ECS task role | `bedrock:InvokeModelWithResponseStream` | Approved model/profile ARNs | Keep only if future ConverseStream/streaming is enabled. |
| ECS task role | `bedrock:ListFoundationModels` | `*` | Not granted by default; only needed if model discovery is enabled after approval. |
| ECS task role | `secretsmanager:GetSecretValue` | Gateway secret ARNs | Restrict by VPC endpoint condition where possible. |
| ECS task role | `kms:Decrypt` | Secret KMS key | Limit to keys used for gateway runtime secrets. |

Production should prefer explicit aliases and leave Bedrock model discovery disabled to avoid needing broad discovery permission.
