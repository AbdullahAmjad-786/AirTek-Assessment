using System.Threading.Tasks;
using System.Collections.Generic;
using Pulumi;
using Aws = Pulumi.Aws;
using Pulumi.Awsx.Ec2.Inputs;
using Ec2 = Pulumi.Awsx.Ec2;
using Eks = Pulumi.Eks;
using Awsx = Pulumi.Awsx;
using Kubernetes = Pulumi.Kubernetes;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Helm;
using Pulumi.Kubernetes.Types.Inputs.Extensions.V1Beta1;
using Pulumi.Eks;

class Program
{
    static Task<int> Main()
    {
        return Deployment.RunAsync(() => {
            var config = new Config();
            var minClusterSize = config.GetInt32("minClusterSize");
            var maxClusterSize = config.GetInt32("maxClusterSize");
            var desiredClusterSize = config.GetInt32("desiredClusterSize");
            var eksNodeInstanceType = config.Get("eksNodeInstanceType");
            var vpcNetworkCidr = config.Get("vpcNetworkCidr");

            // Tags to apply to resources
            var tags = new InputMap<string>
            {
                { "Environment", "Production" },
                { "Project", "AirTek" },
            };

            // Create a new ECR
            var repo = new Awsx.Ecr.Repository("airTek-repo", new()
            {
                Tags = tags,
            });

            //Create & Push Image for Web App
            var appImage = new Awsx.Ecr.Image("web-app-image", new()
            {
                RepositoryUrl = repo.Url,
                Path = "./infra-web",
            });

            //Create & Push Image for Web Api
            var apiImage = new Awsx.Ecr.Image("web-api-image", new()
            {
                RepositoryUrl = repo.Url,
                Path = "./infra-api",
            });

            // Create a new VPC
            var vpc = new Ec2.Vpc("airTek-vpc", new Ec2.VpcArgs
            {
                EnableDnsHostnames = true,
                CidrBlock = vpcNetworkCidr,
                Tags= tags,
                SubnetSpecs =
                {
                    new SubnetSpecArgs
                    {
                        Type = Ec2.SubnetType.Public,
                        CidrMask = 22,
                    },
                    new SubnetSpecArgs
                    {
                        Type = Ec2.SubnetType.Private,
                        CidrMask = 20,
                    }
                }
            });

            // Security Group for Eks Cluster
            var securityGroup = new Aws.Ec2.SecurityGroup("eks-sg", new Aws.Ec2.SecurityGroupArgs
            {
                Description = "Security Group for EKS Cluster",
                VpcId = vpc.VpcId,

            });

            // EKS cluster
            var cluster = new Eks.Cluster("airTek-cluster", new()
            {
                //VPC Configurations
                VpcId = vpc.VpcId,
                PublicSubnetIds = vpc.PublicSubnetIds,
                PrivateSubnetIds = vpc.PrivateSubnetIds,

                //Cluster Configurations
                InstanceType = eksNodeInstanceType,
                DesiredCapacity = desiredClusterSize,
                MinSize = minClusterSize,
                MaxSize = maxClusterSize,

                NodeAssociatePublicIpAddress = false,
                Tags= tags,
            });

            var eksProvider = new Kubernetes.Provider("eks-provider", new()
            {
                KubeConfig = cluster.KubeconfigJson,
            });

            // Node Security Group for Eks Cluster
            var nodeSecurityGroup = new Eks.NodeGroupSecurityGroup("eks-nsg", new NodeGroupSecurityGroupArgs
            {
                ClusterSecurityGroup = securityGroup,
                EksCluster = cluster.EksCluster,
                VpcId = vpc.VpcId,
                Tags = tags,
            }, new ComponentResourceOptions
            {
                Provider = eksProvider,
            }) ; 

            // Kubernetes namespace for the web app and API
            var webAppNamespace = new Kubernetes.Core.V1.Namespace("airTek-namespace", new NamespaceArgs
            {
                Metadata = new ObjectMetaArgs
                {
                    Name = "airTek-prod",
                },
            }, new CustomResourceOptions
            {
                Provider = eksProvider,
            });

            // Install Nginx Ingress Controller using Helm
            var nginxIngress = new Kubernetes.Helm.V3.Chart("nginx-ingress", new ChartArgs
            {
                Chart = "nginx-ingress",
                Version = "4.0.0",
                Namespace = "ingress-controller",
                FetchOptions = new ChartFetchArgs
                {
                    Repo = "https://charts.helm.sh/stable"
                },
            }, new ComponentResourceOptions
            {
                Provider = eksProvider,
            });

            // Deploy Web App
            var appDeployment = new Kubernetes.Apps.V1.Deployment("web-app", new()
            {
                Metadata = new Kubernetes.Types.Inputs.Meta.V1.ObjectMetaArgs
                {
                    Namespace = webAppNamespace.Metadata.Apply(meta => meta.Name),
                    Labels =
                    {
                        { "app", "web-app" },
                    },
                },
                Spec = new Kubernetes.Types.Inputs.Apps.V1.DeploymentSpecArgs
                {
                    Replicas = 3,
                    Selector = new Kubernetes.Types.Inputs.Meta.V1.LabelSelectorArgs
                    {
                        MatchLabels =
                        {
                            { "app", "web-app" },
                        },
                    },
                    Template = new Kubernetes.Types.Inputs.Core.V1.PodTemplateSpecArgs
                    {
                        Metadata = new Kubernetes.Types.Inputs.Meta.V1.ObjectMetaArgs
                        {
                            Labels =
                            {
                                { "app", "web-app" },
                            },
                        },
                        Spec = new Kubernetes.Types.Inputs.Core.V1.PodSpecArgs
                        {
                            Containers = new[]
                            {
                                new Kubernetes.Types.Inputs.Core.V1.ContainerArgs
                                {
                                    Name = "web-app-container",
                                    Image = appImage.ImageUri,
                                    Ports = new[]
                                    {
                                        new Kubernetes.Types.Inputs.Core.V1.ContainerPortArgs
                                        {
                                            Name = "http",
                                            ContainerPortValue = 80,
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            }, new CustomResourceOptions
            {
                Provider = eksProvider,
            });

            // Deploy Web Api
            var apiDeployment = new Kubernetes.Apps.V1.Deployment("web-api", new()
            {
                Metadata = new Kubernetes.Types.Inputs.Meta.V1.ObjectMetaArgs
                {
                    Namespace = webAppNamespace.Metadata.Apply(meta => meta.Name),
                    Labels =
                    {
                        { "app", "web-api" },
                    },
                },
                Spec = new Kubernetes.Types.Inputs.Apps.V1.DeploymentSpecArgs
                {
                    Replicas = 3,
                    Selector = new Kubernetes.Types.Inputs.Meta.V1.LabelSelectorArgs
                    {
                        MatchLabels =
                        {
                            { "app", "web-api" },
                        },
                    },
                    Template = new Kubernetes.Types.Inputs.Core.V1.PodTemplateSpecArgs
                    {
                        Metadata = new Kubernetes.Types.Inputs.Meta.V1.ObjectMetaArgs
                        {
                            Labels =
                            {
                                { "app", "web-api" },
                            },
                        },
                        Spec = new Kubernetes.Types.Inputs.Core.V1.PodSpecArgs
                        {
                            Containers = new[]
                            {
                                new Kubernetes.Types.Inputs.Core.V1.ContainerArgs
                                {
                                    Name = "web-api-container",
                                    Image = apiImage.ImageUri,
                                    Ports = new[]
                                    {
                                        new Kubernetes.Types.Inputs.Core.V1.ContainerPortArgs
                                        {
                                            Name = "http",
                                            ContainerPortValue = 80,
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            }, new CustomResourceOptions
            {
                Provider = eksProvider,
            });

            //Deploy Web App Service
            var webAppService = new Kubernetes.Core.V1.Service("web-app-service", new()
            {
                Metadata = new Kubernetes.Types.Inputs.Meta.V1.ObjectMetaArgs
                {
                    Namespace = webAppNamespace.Metadata.Apply(meta => meta.Name),
                    Labels =
                    {
                        { "app", "web-app" },
                    },
                },
                Spec = new Kubernetes.Types.Inputs.Core.V1.ServiceSpecArgs
                {
                    Ports = new[]
                    {
                        new Kubernetes.Types.Inputs.Core.V1.ServicePortArgs
                        {
                            Port = 80,
                            TargetPort = "http",
                        },
                    },
                    Selector =
                    {
                        { "app", "web-app" },
                    },
                },
            }, new CustomResourceOptions
            {
                Provider = eksProvider,
            });

            //Deploy Web Api Service
            var webApiService = new Kubernetes.Core.V1.Service("web-api-service", new()
            {
                Metadata = new Kubernetes.Types.Inputs.Meta.V1.ObjectMetaArgs
                {
                    Namespace = webAppNamespace.Metadata.Apply(meta => meta.Name),
                    Labels =
                    {
                        { "app", "web-api" },
                    },
                },
                Spec = new Kubernetes.Types.Inputs.Core.V1.ServiceSpecArgs
                {
                    Ports = new[]
                    {
                        new Kubernetes.Types.Inputs.Core.V1.ServicePortArgs
                        {
                            Port = 80,
                            TargetPort = "http",
                        },
                    },
                    Selector =
                    {
                        { "app", "web-api" },
                    },
                },
            }, new CustomResourceOptions
            {
                Provider = eksProvider,
            });

            // Ingress resource for the web app
            var webAppIngress = new Kubernetes.Extensions.V1Beta1.Ingress("web-app-ingress", new IngressArgs
            {
                Metadata = new ObjectMetaArgs
                {
                    Namespace = webAppNamespace.Metadata.Apply(meta => meta.Name),
                    Annotations = new InputMap<string>
                    {
                        { "nginx.ingress.kubernetes.io/rewrite-target", "/" },
                    },
                },
                Spec = new IngressSpecArgs
                {
                    Rules = new[]
                    {
                        new IngressRuleArgs
                        {
                            Host = "airtek-web-app.com", 
                            Http = new HTTPIngressRuleValueArgs
                            {
                                Paths = new[]
                                {
                                    new HTTPIngressPathArgs
                                    {
                                        Path = "/",
                                        Backend = new IngressBackendArgs
                                        {
                                            ServiceName = webAppService.Metadata.Apply(meta => meta.Name),
                                            ServicePort = webAppService.Spec.Apply(spec => spec.Ports[0].Port),
                                        },
                                    },
                                },
                            },
                        },
                    },
                },
            }, new CustomResourceOptions
            {
                Provider = eksProvider,
            });

            // Export the ECR, EKS cluster information
            return new Dictionary<string, object?>
            {
                { "repositoryName", repo.Url },
                { "clusterName", cluster.GetResourceName() },
            };
        });
    }
}
